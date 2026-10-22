using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Network;

namespace Content.Server.Discord;

/// <summary>The journal is the transactional outbox. A lease prevents simultaneous delivery by game slots.</summary>
public sealed partial class LagoonCoinWebhookSystem : EntitySystem
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IConfigurationManager _config = default!;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly CancellationTokenSource _stop = new();
    private readonly Guid _owner = Guid.NewGuid();
    private static readonly Regex WebhookUrl = new(@"^https://(?:discord\.com|discordapp\.com)/api(?:/v\d+)?/webhooks/[0-9]+/[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);
    private volatile string _url = "";
    private volatile string _server = "";

    public override void Initialize()
    {
        base.Initialize();
        _config.OnValueChanged(CCVars.DiscordLagoonCoinWebhook, SetWebhook, true);
        _config.OnValueChanged(CCVars.AdminLogsServerName, SetServer, true);
        _ = Task.Run(Deliver);
    }

    private void SetServer(string value) => _server = value;
    private void SetWebhook(string value)
    {
        value = value.Trim().TrimEnd('/');
        _url = WebhookUrl.IsMatch(value) ? value : "";
        if (value.Length != 0 && _url.Length == 0)
            Log.Error("Invalid discord.lagoon_coin_webhook; expected a Discord webhook URL.");
    }

    private async Task<string> Name(Guid? id, CancellationToken cancel)
        => id == null ? "Система" : (await _database.GetPlayerRecordByUserId(new NetUserId(id.Value), cancel))?.LastSeenUserName ?? "Неизвестный игрок";

    private async Task Deliver()
    {
        var cancel = _stop.Token;
        while (!cancel.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(2);
            LagoonCoinOperation? operation = null;
            try
            {
                var url = _url;
                if (url.Length != 0 && (operation = await _database.ClaimLagoonCoinWebhookAsync(_owner, cancel)) != null)
                {
                    var payload = LagoonCoinWebhookFormatter.Format(operation, _server,
                        await Name(operation.UserId, cancel), await Name(operation.ActorId, cancel));
                    var result = await Send(_http, _database, url, operation, _owner, payload, cancel);
                    delay = result.RetryAfter;
                    if (!result.Delivered)
                        Log.Warning($"LC webhook HTTP {result.StatusCode}; journal entry retained for retry.");
                    else
                    {
                        Log.Info($"Delivered LC webhook: {operation.OperationId}.");
                        continue;
                    }
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                // Never include the URL, response body, reason, or exception message containing secrets.
                Log.Warning($"LC webhook delivery failed: {e.GetType().Name}; journal entry retained for retry.");
                delay = TimeSpan.FromSeconds(30);
                if (operation != null)
                {
                    try
                    {
                        await _database.RetryLagoonCoinWebhookAsync(operation.UserId, operation.OperationId,
                            _owner, DateTime.UtcNow + delay, cancel);
                    }
                    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { break; }
                    catch (Exception retryError) when (!cancel.IsCancellationRequested)
                    {
                        Log.Warning($"Could not reschedule LC webhook: {retryError.GetType().Name}; lease will expire automatically.");
                    }
                }
            }
            try { await Task.Delay(delay, cancel); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { break; }
        }
    }

    public sealed record DeliveryResult(bool Delivered, TimeSpan RetryAfter, int StatusCode);

    public static async Task<DeliveryResult> Send(HttpClient http, IServerDbManager database, string url,
        LagoonCoinOperation operation, Guid owner, WebhookPayload payload, CancellationToken cancel = default)
    {
        using var response = await http.PostAsJsonAsync(url + "?wait=true", payload, cancel);
        if (response.IsSuccessStatusCode)
        {
            await database.CompleteLagoonCoinWebhookAsync(operation.UserId, operation.OperationId, owner, cancel);
            return new DeliveryResult(true, TimeSpan.Zero, (int)response.StatusCode);
        }
        var delay = TimeSpan.FromSeconds(30);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            if (response.Headers.RetryAfter?.Delta is { } header) delay = header;
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            if (body.RootElement.TryGetProperty("retry_after", out var retry) && retry.TryGetDouble(out var seconds) && double.IsFinite(seconds))
                delay = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 3600));
        }
        delay = TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 3600));
        await database.RetryLagoonCoinWebhookAsync(operation.UserId, operation.OperationId, owner, DateTime.UtcNow + delay, cancel);
        return new DeliveryResult(false, delay, (int)response.StatusCode);
    }

    public override void Shutdown()
    {
        _config.UnsubValueChanged(CCVars.DiscordLagoonCoinWebhook, SetWebhook);
        _config.UnsubValueChanged(CCVars.AdminLogsServerName, SetServer);
        _stop.Cancel();
        _http.Dispose();
        base.Shutdown();
    }
}

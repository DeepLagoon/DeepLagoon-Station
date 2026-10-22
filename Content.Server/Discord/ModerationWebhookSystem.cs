using System.IO;
using System.Linq;
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
using Robust.Shared.ContentPack;
using Robust.Shared.Network;

namespace Content.Server.Discord;

/// <summary>Per-runtime persistent outbox. Discord outages never prevent moderation actions.</summary>
public sealed partial class ModerationWebhookSystem : EntitySystem
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IResourceManager _resources = default!;

    private HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly CancellationTokenSource _stop = new();
    private static readonly Regex WebhookUrl = new(@"^https://(?:discord\.com|discordapp\.com)/api(?:/v\d+)?/webhooks/[0-9]+/[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);
    private volatile string _url = "";
    private string _server = "";
    private string? _outbox;
    private Task? _worker;

    public sealed record Pending(ModerationEvent Change, string Server, List<WebhookPayload>? Payloads = null, int Next = 0);

    public override void Initialize()
    {
        base.Initialize();
        if (_resources.UserData.RootDir is {} root)
            _outbox = Path.Combine(root, "moderation-webhooks");
        _config.OnValueChanged(CCVars.DiscordModerationWebhook, SetWebhook, true);
        _config.OnValueChanged(CCVars.AdminLogsServerName, SetServer, true);
        _database.ModerationChanged += OnModeration;
        _worker = Task.Run(Deliver);
    }

    private void SetServer(string value) => _server = value;

    private void SetWebhook(string value)
    {
        value = value.Trim().TrimEnd('/');
        _url = WebhookUrl.IsMatch(value) ? value : "";
        if (value.Length != 0 && _url.Length == 0)
            Log.Error("Invalid discord.moderation_webhook; expected a Discord webhook URL.");
        if (_url.Length != 0 && _outbox == null)
            Log.Error("Moderation webhooks require a persistent server data directory.");
    }

    private void OnModeration(ModerationEvent change)
    {
        if (_url.Length == 0 || _outbox == null || !ModerationWebhookFormatter.ShouldNotify(change))
            return;
        try
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(_outbox);
            else
                Directory.CreateDirectory(_outbox, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var path = Path.Combine(_outbox, $"{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}.json");
            Save(path, new Pending(change, _server));
        }
        catch (Exception e)
        {
            Log.Error($"Could not queue moderation notification: {e.GetType().Name}");
        }
    }

    private static void Save(string path, Pending pending)
    {
        var temporary = path + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(file, pending);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }

    private async Task<string> PlayerName(NetUserId? id)
        => id == null ? "Система / без аккаунта" : (await _database.GetPlayerRecordByUserId(id.Value))?.LastSeenUserName ?? "Неизвестный игрок";

    private async Task Deliver()
    {
        var cancellation = _stop.Token;
        while (!cancellation.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(2);
            try
            {
                if (_url.Length != 0 && _outbox != null && Directory.Exists(_outbox))
                {
                    var path = Directory.EnumerateFiles(_outbox, "*.json").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
                    if (path != null)
                    {
                        Pending pending;
                        try
                        {
                            pending = JsonSerializer.Deserialize<Pending>(await File.ReadAllTextAsync(path, cancellation))
                                ?? throw new JsonException();
                        }
                        catch (JsonException)
                        {
                            // Retain the damaged record for inspection, without blocking following events.
                            File.Move(path, path + ".invalid", true);
                            Log.Error("Invalid moderation outbox record; retained as .invalid.");
                            continue;
                        }
                        // Apply the policy to old outbox entries too, including already formatted payloads.
                        if (!ModerationWebhookFormatter.ShouldNotify(pending.Change))
                        {
                            File.Delete(path);
                            continue;
                        }
                        if (pending.Payloads == null)
                        {
                            pending = pending with { Payloads = ModerationWebhookFormatter.Format(pending.Change, pending.Server,
                                await PlayerName(pending.Change.Player),
                                await PlayerName(pending.Change.Actor is {} actor ? new NetUserId(actor) : null)) };
                            Save(path, pending);
                        }
                        if (pending.Next >= pending.Payloads.Count)
                        {
                            File.Delete(path);
                            continue;
                        }
                        var url = _url;
                        if (url.Length == 0)
                            continue;
                        using var response = await _http.PostAsJsonAsync(url + "?wait=true", pending.Payloads[pending.Next], cancellation);
                        if (response.IsSuccessStatusCode)
                        {
                            pending = pending with { Next = pending.Next + 1 };
                            Save(path, pending);
                            Log.Info($"Delivered moderation notification: {pending.Change.Type} #{pending.Change.Id}, {pending.Change.Action}, part {pending.Next}.");
                            if (pending.Next >= pending.Payloads.Count)
                                File.Delete(path);
                            continue;
                        }
                        Log.Warning($"Moderation webhook HTTP {(int) response.StatusCode}; retained for retry.");
                        delay = TimeSpan.FromSeconds(30);
                        if (response.StatusCode == HttpStatusCode.TooManyRequests)
                        {
                            if (response.Headers.RetryAfter?.Delta is {} headerDelay)
                                delay = headerDelay;
                            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
                            if (body.RootElement.TryGetProperty("retry_after", out var retry) && retry.TryGetDouble(out var seconds) && double.IsFinite(seconds))
                                delay = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 3600));
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                // Exception messages may include the webhook token or private note. Never log them.
                Log.Warning($"Moderation webhook delivery failed: {e.GetType().Name}; retained for retry.");
                delay = TimeSpan.FromSeconds(30);
            }
            try { await Task.Delay(delay, cancellation); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override void Shutdown()
    {
        _database.ModerationChanged -= OnModeration;
        _config.UnsubValueChanged(CCVars.DiscordModerationWebhook, SetWebhook);
        _config.UnsubValueChanged(CCVars.AdminLogsServerName, SetServer);
        _stop.Cancel();
        if (_worker != null)
            _ = _worker.ContinueWith(_ => { _http.Dispose(); _stop.Dispose(); }, TaskScheduler.Default);
        base.Shutdown();
    }
}

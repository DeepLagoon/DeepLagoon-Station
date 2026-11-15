using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Robust.Server.ServerStatus;
using Robust.Shared.Configuration;

namespace Content.Server._DeepLagoon.Currency;

[CVarDefs]
public sealed class FinanceAuditCVars
{
    public static readonly CVarDef<string> Token =
        CVarDef.Create("finance_audit.bot_token", "", CVar.SERVERONLY | CVar.CONFIDENTIAL);
}

/// <summary>A loopback-only relay. The bot reads the committed journal and acknowledges Discord delivery.</summary>
public sealed class FinanceAuditSystem : EntitySystem
{
    private const string PathPrefix = "/deeplagoon/finance/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IStatusHost _status = default!;

    private readonly SemaphoreSlim _request = new(1);
    private volatile byte[] _authorization = [];

    public override void Initialize()
    {
        _configuration.OnValueChanged(FinanceAuditCVars.Token, SetToken, true);
        _status.AddHandler(Handle);
    }

    public override void Shutdown()
    {
        _configuration.UnsubValueChanged(FinanceAuditCVars.Token, SetToken);
        _authorization = [];
    }

    private void SetToken(string token)
        => _authorization = token is { Length: >= 32 and <= 128 } ? Encoding.UTF8.GetBytes("Bearer " + token) : [];

    private async Task<bool> Handle(IStatusHandlerContext context)
    {
        var path = context.Url.AbsolutePath;
        if (!path.StartsWith(PathPrefix, StringComparison.Ordinal))
            return false;
        context.ResponseHeaders["Cache-Control"] = "no-store";
        var topic = path[PathPrefix.Length..];
        var authorization = _authorization;
        var peer = context.RemoteEndPoint.Address;
        if (peer.IsIPv4MappedToIPv6)
            peer = peer.MapToIPv4();
        if (authorization.Length == 0 || !IPAddress.IsLoopback(peer) ||
            !context.RequestHeaders.TryGetValue("Authorization", out var header) ||
            !CryptographicOperations.FixedTimeEquals(authorization, Encoding.UTF8.GetBytes(header.ToString())))
        {
            await context.RespondErrorAsync(HttpStatusCode.Unauthorized);
            return true;
        }
        if (topic is not ("bank" or "alerts" or "lc"))
        {
            await context.RespondErrorAsync(HttpStatusCode.NotFound);
            return true;
        }
        if (!await _request.WaitAsync(0))
        {
            await context.RespondErrorAsync(HttpStatusCode.TooManyRequests);
            return true;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (context.RequestMethod == HttpMethod.Get)
            {
                var events = await _database.ReadFinanceQueue(topic, timeout.Token);
                await context.RespondJsonAsync(JsonSerializer.SerializeToElement(events, Json));
            }
            else if (context.RequestMethod == HttpMethod.Post)
            {
                var ids = await ReadIds(context.RequestBody, timeout.Token);
                if (ids is not { Length: >= 1 and <= 15 } || ids.Any(id =>
                        id == null || id.UserId == Guid.Empty || id.OperationId is not { Length: >= 1 and <= 200 } ||
                        (topic != "lc" && !Guid.TryParseExact(id.OperationId, "N", out _))))
                {
                    await context.RespondErrorAsync(HttpStatusCode.BadRequest);
                    return true;
                }
                await _database.AcknowledgeFinanceQueue(topic, ids, timeout.Token);
                await context.RespondNoContentAsync();
            }
            else
                await context.RespondErrorAsync(HttpStatusCode.MethodNotAllowed);
        }
        catch (JsonException)
        {
            await context.RespondErrorAsync(HttpStatusCode.BadRequest);
        }
        catch (Exception ex)
        {
            Log.Warning($"Finance audit relay unavailable: {ex.GetType().Name}");
            await context.RespondErrorAsync(HttpStatusCode.ServiceUnavailable);
        }
        finally { _request.Release(); }
        return true;
    }

    private static async Task<FinanceQueueId[]?> ReadIds(Stream source, CancellationToken cancel)
    {
        var buffer = new byte[8193];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(count), cancel);
            if (read == 0)
                return JsonSerializer.Deserialize<FinanceQueueId[]>(buffer.AsSpan(0, count), Json);
            count += read;
        }
        return null;
    }
}

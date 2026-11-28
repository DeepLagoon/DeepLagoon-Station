using System.Net;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Currency;
using Content.Server.Database;
using Robust.Server.ServerStatus;
using Robust.Shared.Network;

namespace Content.Server._DeepLagoon.DiscordLink;

public sealed partial class DiscordLinkSystem
{
    // HandleApi authenticates the loopback bot token; only the bot verifies Discord roles.
    private async Task<bool> HandleLagoonCoinApi(IStatusHandlerContext context)
    {
        LagoonCoinRequest? request;
        try
        {
            var buffer = new byte[4097];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await context.RequestBody.ReadAsync(buffer.AsMemory(count));
                if (read == 0) break;
                count += read;
            }
            request = count > 4096 ? null : JsonSerializer.Deserialize<LagoonCoinRequest>(buffer.AsSpan(0, count));
        }
        catch (JsonException) { request = null; }
        var history = context.Url.AbsolutePath.EndsWith("/lc_history", StringComparison.Ordinal);
        if (request == null || !request.HostAuthorized || (!history && request.Amount <= 0) ||
            !ValidDiscordId(request.DiscordId) || !ValidDiscordId(request.ActorDiscordId) ||
            !ValidDiscordId(request.RequestId) || request.Page is < 1 or > 100000 ||
            (!history && (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 400)))
        {
            await context.RespondJsonAsync(new { error = "invalid_request" }, HttpStatusCode.BadRequest);
            return true;
        }
        await _apiLock.WaitAsync();
        try
        {
            var result = await OnMainThread(async () =>
            {
                if (!_enabled || _store == null)
                    return new ApiResult(HttpStatusCode.ServiceUnavailable, new { error = "unavailable" });
                var target = _store.FindDiscord(request.DiscordId);
                if (target == null)
                    return new ApiResult(HttpStatusCode.NotFound, new { error = "not_linked" });
                if (!history && _store.MergePending(target.Uid))
                    return new ApiResult(HttpStatusCode.Conflict, new { error = "merge_pending" });
                if (history)
                {
                    var rows = await _database.GetLagoonCoinHistoryAsync(new NetUserId(target.Uid), request.Page);
                    return new ApiResult(HttpStatusCode.OK, new { uid = target.Uid, request_id = request.RequestId,
                        balance = await _database.GetLagoonCoinsAsync(new NetUserId(target.Uid)),
                        has_more = rows.Count > 20, rows = rows.Take(20).Select(r => new {
                            amount = r.Amount, reason = r.Reason, actor_uid = r.ActorId,
                            created_at = r.CreatedAt, operation_id = r.OperationId }) });
                }
                var author = _store.FindDiscord(request.ActorDiscordId);
                var wallet = EntityManager.System<LagoonCoinSystem>();
                var uid = new NetUserId(target.Uid);
                NetUserId? actor = author == null ? null : new NetUserId(author.Uid);
                var reason = $"Discord actor={request.ActorDiscordId}; {request.Reason.Trim()}";
                LagoonCoinResult reward;
                try
                {
                    reward = context.Url.AbsolutePath.EndsWith("/lc_remove", StringComparison.Ordinal)
                        ? await wallet.Deduct(uid, request.Amount, reason, actor, $"discord:{request.RequestId}")
                        : await wallet.Grant(uid, request.Amount, reason, actor, $"discord:{request.RequestId}");
                }
                catch (InsufficientLagoonCoinsException)
                {
                    return new ApiResult(HttpStatusCode.Conflict, new { error = "insufficient_lc" });
                }
                return new ApiResult(HttpStatusCode.OK, new { uid = target.Uid, balance = reward.Balance,
                    applied = reward.Applied, request_id = request.RequestId });
            });
            await context.RespondJsonAsync(result.Body, result.Status);
        }
        catch (Exception e)
        {
            Log.Error($"Discord LC operation failed: {Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)}");
            await context.RespondJsonAsync(new { error = "unavailable" }, HttpStatusCode.ServiceUnavailable);
        }
        finally { _apiLock.Release(); }
        return true;
    }

    private static bool ValidDiscordId(string? value)
        => value is { Length: >= 15 and <= 20 } && ulong.TryParse(value, out var id) && id > 0 &&
           System.Linq.Enumerable.All(value, char.IsAsciiDigit);

    private sealed record LagoonCoinRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("discord_id")] string DiscordId,
        [property: System.Text.Json.Serialization.JsonPropertyName("actor_discord_id")] string ActorDiscordId,
        [property: System.Text.Json.Serialization.JsonPropertyName("request_id")] string RequestId,
        [property: System.Text.Json.Serialization.JsonPropertyName("amount")] long Amount,
        [property: System.Text.Json.Serialization.JsonPropertyName("reason")] string Reason,
        [property: System.Text.Json.Serialization.JsonPropertyName("host_authorized")] bool HostAuthorized,
        [property: System.Text.Json.Serialization.JsonPropertyName("page")] int Page = 1);
}

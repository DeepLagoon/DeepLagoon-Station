using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Content.Server._DeepLagoon.DiscordLink;

public sealed partial class DiscordLinkSystem
{
    private readonly Dictionary<string, Task<DiscordLinkStore.Link>> _mergeJobs = new();

    private async Task<ApiResult> HandleMergeRequest(string discordId, string code)
    {
        if (!_enabled || _store == null)
            return new ApiResult(HttpStatusCode.ServiceUnavailable, new { error = "unavailable" });
        if (!_store.IsMergeCode(code)) return new ApiResult(HttpStatusCode.Continue, new { });
        // Neither raw one-time codes nor credentials are used as visible job identifiers.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(discordId + ":" + code)));
        if (!_mergeJobs.TryGetValue(key, out var job))
        {
            if (_mergeJobs.Count >= 4)
                return new ApiResult(HttpStatusCode.TooManyRequests, new { error = "merge_pending" });
            job = MergeAccounts(discordId, code);
            _mergeJobs.Add(key, job);
            _ = ObserveMerge(key, job);
        }
        try
        {
            // Timeout only the HTTP wait. The worker may be committing its transaction.
            var link = await job.WaitAsync(TimeSpan.FromSeconds(20));
            await RefreshUid(link.Uid);
            return new ApiResult(HttpStatusCode.OK, new { uid = link.Uid, username = link.Username, existing = false, merged = true });
        }
        catch (TimeoutException)
        {
            return new ApiResult(HttpStatusCode.Conflict, new { error = "merge_pending" });
        }
        catch (DiscordLinkStore.LinkException e)
        {
            return new ApiResult(HttpStatusCode.Conflict, new { error = e.Message });
        }
        catch (Exception e)
        {
            Log.Error($"Account merge failed: {Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)}");
            return new ApiResult(HttpStatusCode.ServiceUnavailable, new { error = "merge_pending" });
        }
    }

    private async Task ObserveMerge(string key, Task<DiscordLinkStore.Link> job)
    {
        try { await job; }
        catch (Exception e) { Log.Warning($"Account merge job retained for retry: {Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)}"); }
        finally { _mergeJobs.Remove(key); }
    }
}

using Content.Server.Database;

namespace DeepLagoon.AccountService;

public sealed partial class AccountOperations
{
    private async Task<object> RewardBugReport(AccountRequest request, CancellationToken cancel)
    {
        var legacyCategory = request.Category == null;
        var category = request.Category ?? "bug:code";
        // The authenticated Discord bot attests the category-matched executor's confirmation.
        // This policy grants only the fixed reporter bounty, never arbitrary staff LC access.
        if (!DiscordId(request.DiscordId) || !DiscordId(request.ActorDiscordId) ||
            !DiscordId(request.RequestId) || !DiscordId(request.WorkChannel) || request.Amount != 10 ||
            category is not ("bug:code" or "bug:sprite" or "bug:translation" or "bug:map" or "bug:wiki"))
            throw new AccountError("invalid_request", 400);
        var target = FindLink(request.DiscordId) ?? throw new AccountError("not_linked", 404);
        var author = FindLink(request.ActorDiscordId!) ?? throw new AccountError("actor_not_linked", 403);
        AssertStableLink(request.DiscordId, target.Uid);
        AssertStableLink(request.ActorDiscordId!, author.Uid);
        await using var db = settings.OpenDatabase();
        if (legacyCategory || target.Uid == author.Uid)
        {
            if (!request.HostAuthorized)
                throw new AccountError(legacyCategory ? "legacy_category_requires_host" : "self_confirmation", 403);
            await RequireHost(db, author.Uid, cancel);
        }
        var receipt = request with { RequestId = request.WorkChannel, Category = category,
            Reason = $"Bug report {request.WorkChannel}; category={category}" };
        var actor = BindReceipt(receipt, "bug_report_reward", target, author.Uid);
        ReserveBudget($"bug-report:{request.WorkChannel}", 10);
        var result = await AccountCurrencyOperations.AwardLagoonCoins(db, target.Uid,
            $"discord-bug-report:{request.WorkChannel}", 10,
            $"Discord actor={request.ActorDiscordId}; {receipt.Reason}", actor,
            cancel: cancel, beforeCommit: () => {
                AssertStableLink(request.DiscordId, target.Uid);
                AssertStableLink(request.ActorDiscordId!, author.Uid);
            });
        return new { uid = target.Uid, balance = result.Balance, applied = result.Applied, request_id = request.RequestId };
    }
}

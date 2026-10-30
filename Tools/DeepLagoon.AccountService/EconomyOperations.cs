using System.Text.Json;
using System.Text.Json.Serialization;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace DeepLagoon.AccountService;

public sealed record EconomyRequest(string DiscordId, string ActorDiscordId, string RequestId,
    bool HostAuthorized = false, string? TaskId = null, string? Category = null, int Amount = 0, string? Reason = null,
    int Tier = 0, string? Receipt = null, DateTimeOffset PaidAt = default, bool Historical = false,
    string? Color = null, string? ReferralCode = null, int ExternalTier = 0, string? NewcomerDiscordId = null, DateTimeOffset ObservedAt = default, string? OperationId = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ExpectedUid = null);

public sealed partial class AccountOperations
{
    private static async Task<object[]> ShopReferrals(ServerDbContext db, Guid user, CancellationToken cancel)
    {
        // Scope the SQL query before decoding state, then verify the actual referral edge.
        var owner = user.ToString();
        var rows = await (from account in db.LagoonEconomyRecords.AsNoTracking()
            where account.Id.StartsWith("account:") && account.Data.Contains(owner)
            join player in db.Player.AsNoTracking() on account.UserId equals player.UserId
            select new { account.Data, player.LastSeenUserName }).ToListAsync(cancel);
        return rows.Select(row => (State: JsonSerializer.Deserialize<LagoonEconomyState>(row.Data), Name: row.LastSeenUserName))
            .Where(row => row.State?.Referrer == user)
            .Select(row => new { name = row.Name, stage = row.State!.ReferralStage,
                reward = row.State.ReferrerReward ? row.State.ReferralStage >= 2 ? 100 : row.State.ReferralStage == 1 ? 25 : 0 : 0 })
            .OrderBy(row => row.name, StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray();
    }

    private static async Task<object> EconomyStatus(ServerDbContext db, Guid user, EconomyRequest request, CancellationToken cancel)
    {
        if ((request.TaskId != null ? 1 : 0) + (request.Receipt != null ? 1 : 0) + (request.OperationId != null ? 1 : 0) != 1)
            throw new AccountError("invalid_request", 400);
        var id = request.TaskId != null ? "task:" + request.TaskId : request.Receipt != null ? "payment:" + request.Receipt : null;
        if (id != null)
        {
            var record = await db.LagoonEconomyRecords.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.UserId == user, cancel);
            var status = record == null ? "not_found" : id.StartsWith("task:", StringComparison.Ordinal) && JsonSerializer.Deserialize<LagoonTask>(record.Data)?.Approver == null ? "declared" : "committed";
            return new { request_id = request.RequestId, id, status, record = record == null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(record.Data) };
        }
        if (request.OperationId is not { Length: >= 1 and <= 150 }) throw new AccountError("invalid_request", 400);
        var key = request.OperationId;
        var row = await db.LagoonCoinOperations.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == user && p.OperationId == key, cancel);
        return new { request_id = request.RequestId, id = key, status = row == null ? "not_found" : "committed",
            amount = row?.Amount, balance_after = row?.BalanceAfter, reason = row?.Reason, actor = row?.ActorId, at = row?.CreatedAt };
    }

    public async Task<object> ExecuteEconomy(string action, EconomyRequest request, CancellationToken cancel = default)
    {
        if (action is not ("economy_state" or "economy_pass" or "economy_color" or "economy_referral" or
            "economy_task_create" or "economy_task_approve" or "economy_payment" or "economy_onboarding" or "economy_supporters" or "economy_supporter" or "economy_status"))
            throw new AccountError("not_found", 404);
        if (!DiscordId(request.DiscordId) || !DiscordId(request.ActorDiscordId) || !DiscordId(request.RequestId) || request.ExternalTier is < 0 or > 4)
            throw new AccountError("invalid_request", 400);
        var staff = action is "economy_task_create" or "economy_task_approve" or "economy_payment" or "economy_onboarding";
        if (staff && !request.HostAuthorized || !staff && request.DiscordId != request.ActorDiscordId && !request.HostAuthorized)
            throw new AccountError("forbidden", 403);
        var target = FindLink(request.DiscordId) ?? throw new AccountError("not_linked", 404);
        if (request.ExpectedUid is { } expected && expected != target.Uid)
            throw new AccountError("not_linked", 409);
        var author = FindLink(request.ActorDiscordId) ?? throw new AccountError("actor_not_linked", 403);
        AssertStableLink(request.DiscordId, target.Uid);
        AssertStableLink(request.ActorDiscordId, author.Uid);
        void Stable() { AssertStableLink(request.DiscordId, target.Uid); AssertStableLink(request.ActorDiscordId, author.Uid); }
        await using var db = settings.OpenDatabase();
        if (staff || request.HostAuthorized)
            await RequireHost(db, author.Uid, cancel);
        // The durable API receipt binds every Discord interaction to its original identities and body.
        if (action is not ("economy_state" or "economy_supporters" or "economy_supporter" or "economy_status"))
            BindReceipt(new AccountRequest(request.DiscordId, request.ActorDiscordId, request.RequestId,
                request.Amount, JsonSerializer.Serialize(request), request.HostAuthorized), action, target, author.Uid);
        // Reservations survive uncertain results and cannot be renewed by a retry.
        if (staff) ReserveBudget(request.RequestId, 1, staff: true);
        var now = DateTime.UtcNow;
        LagoonCoinResult? result = null;
        try
        {
            switch (action)
            {
                case "economy_state": break;
                case "economy_supporter":
                    await AccountEconomyOperations.SyncExternal(db, target.Uid, request.ExternalTier, request.ObservedAt.UtcDateTime, now, cancel, Stable); break;
                case "economy_status":
                    return await EconomyStatus(db, target.Uid, request, cancel);
                case "economy_supporters":
                    return new { request_id = request.RequestId, names = (await AccountEconomyOperations.PublicSupporters(db, cancel)).Select(p => p.Name).ToArray() };
                case "economy_pass":
                    await AccountEconomyOperations.Purchase(db, target.Uid, new LagoonEconomyPurchase("discord:" + request.RequestId,
                        "pass", request.Tier.ToString(), 0, 0), now, cancel, Stable);
                    break;
                case "economy_color":
                    await AccountEconomyOperations.SetColor(db, target.Uid, request.Color ?? "", 0, now, cancel, Stable, "color:discord:" + request.RequestId); break;
                case "economy_referral":
                    await AccountEconomyOperations.BindReferral(db, target.Uid, request.ReferralCode ?? "", now, cancel, Stable); break;
                case "economy_task_create":
                    await AccountEconomyOperations.DefineTask(db, target.Uid, author.Uid, request.TaskId ?? "", request.Category ?? "",
                        request.Amount, request.Reason ?? "", now, cancel, Stable); break;
                case "economy_task_approve":
                    result = await AccountEconomyOperations.ApproveTask(db, target.Uid, author.Uid, request.TaskId ?? "", now, cancel, Stable); break;
                case "economy_onboarding":
                    if (request.NewcomerDiscordId == null || !DiscordId(request.NewcomerDiscordId)) throw new AccountError("invalid_request", 400);
                    var newcomer = FindLink(request.NewcomerDiscordId) ?? throw new AccountError("not_linked", 404);
                    void OnboardingStable() { Stable(); AssertStableLink(request.NewcomerDiscordId, newcomer.Uid); }
                    result = await AccountEconomyOperations.Onboarding(db, target.Uid, author.Uid, newcomer.Uid,
                        request.Category ?? "", request.Reason ?? "", now, cancel, OnboardingStable); break;
                case "economy_payment":
                    result = await AccountEconomyOperations.ConfirmPayment(db, target.Uid, author.Uid, request.Receipt ?? "", request.Tier,
                        target.Username, request.PaidAt.UtcDateTime, request.Historical, now, cancel, Stable); break;
            }
            db.ChangeTracker.Clear();
            var snapshot = await AccountEconomyOperations.Get(db, target.Uid, 0, now, cancel, Stable);
            return new { uid = target.Uid, request_id = request.RequestId, balance = snapshot.Balance,
                applied = result?.Applied, awarded = result?.Awarded, state = snapshot.State,
                referrals = request.ExpectedUid.HasValue ? await ShopReferrals(db, target.Uid, cancel) : [],
                subscriptions = Enumerable.Range(1, 4).Select(t => new { id = t,
                    name = Content.Shared.Database.LagoonEconomyRules.Name(t),
                    price = Content.Shared.Database.LagoonEconomyRules.PassPrice(t),
                    rate = Content.Shared.Database.LagoonEconomyRules.HourlyRate(t) }) };
        }
        catch (InvalidOperationException)
        {
            throw new AccountError("economy_conflict");
        }
    }
}

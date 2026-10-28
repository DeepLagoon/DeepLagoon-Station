using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared.Database;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public static partial class AccountEconomyOperations
{
    public static (int Min, int Max) WorkRange(string kind) => kind switch
    {
        "admin-duty" => (40, 40), "admin-case" => (40, 120),
        "code-tiny" => (50, 100), "code-small" => (150, 300), "code-medium" => (400, 800), "code-large" => (1000, 2000),
        "map-room" => (100, 200), "map-apartment" => (250, 500), "map-poi" => (600, 1200), "map-large" => (1500, 3000),
        "sprite-icon" => (40, 80), "sprite-item" => (120, 250), "sprite-set" => (300, 600), "sprite-large" => (800, 1500),
        "onboard-interview" => (20, 20), "onboard-training" => (40, 40), "onboard-return" => (20, 20),
        _ => throw new ArgumentException("Unknown task category.")
    };
    public static async Task DefineTask(ServerDbContext db, Guid user, Guid creator, string id, string kind, int amount,
        string description, DateTime now, CancellationToken cancel = default, Action? beforeCommit = null)
    {
        var range = WorkRange(kind);
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_.:-]{1,100}$") || amount < range.Min || amount > range.Max || description.Length is < 1 or > 400)
            throw new ArgumentException("Invalid task or reward.");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await AccountCurrencyOperations.LockAccount(db, user, cancel);
        var task = new LagoonTask(user, creator, kind, amount, description);
        var key = "task:" + id;
        var previous = await db.LagoonEconomyRecords.SingleOrDefaultAsync(p => p.Id == key, cancel);
        if (previous != null)
        {
            var old = Read<LagoonTask>(previous);
            if (old with { Approver = null } != task) throw new InvalidOperationException("Task ID is already assigned to different work.");
            return;
        }
        db.LagoonEconomyRecords.Add(new LagoonEconomyRecord { Id = key, UserId = user, Data = Json(task) });
        await db.SaveChangesAsync(cancel); beforeCommit?.Invoke(); await tx.CommitAsync(cancel);
    }
    public static async Task<LagoonCoinResult> ApproveTask(ServerDbContext db, Guid user, Guid approver, string id,
        DateTime now, CancellationToken cancel = default, Action? beforeCommit = null)
    {
        if (user == approver) throw new InvalidOperationException("Нельзя принять собственную работу.");
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_.:-]{1,100}$")) throw new ArgumentException("Invalid task ID.");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await AccountCurrencyOperations.LockAccount(db, user, cancel);
        var (wallet, account, state) = await Load(db, user, now, cancel);
        var record = await db.LagoonEconomyRecords.SingleAsync(p => p.Id == "task:" + id, cancel);
        var task = Read<LagoonTask>(record);
        if (task.User != user) throw new InvalidOperationException("Исполнитель задания не совпадает.");
        if (task.Approver != null) return new(wallet.LagoonCoins, 0, false);
        if (task.Kind == "admin-duty")
        {
            var day = LagoonEconomyRules.MoscowDay(now);
            var month = day[..7];
            if (state.DutyDays.Contains(day) || state.DutyMonths.GetValueOrDefault(month) + task.Amount > 600)
                throw new InvalidOperationException("Лимит дежурства: одна смена в день и 600 LC за месяц.");
            state.DutyDays.Add(day);
            state.DutyMonths[month] = state.DutyMonths.GetValueOrDefault(month) + task.Amount;
        }
        // Onboarding identifiers bind one novice and one stage globally, not a new task per click.
        if (task.Kind.StartsWith("onboard-", StringComparison.Ordinal))
            throw new InvalidOperationException("Для приёма новичка используйте отдельную операцию с его аккаунтом и этапом.");
        Credit(db, wallet, "task:" + id, task.Amount, "team-task:" + task.Kind + ":" + id, now, approver);
        record.Data = Json(task with { Approver = approver });
        Save(account, state); await db.SaveChangesAsync(cancel); beforeCommit?.Invoke(); await tx.CommitAsync(cancel);
        return new(wallet.LagoonCoins, task.Amount, true);
    }
    public static async Task BindReferral(ServerDbContext db, Guid user, string code, DateTime now, CancellationToken cancel = default, Action? beforeCommit = null)
    {
        code = code.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(code, "^[A-F0-9]{20}$")) throw new ArgumentException("Неверный код приглашения.");
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancel);
        await AccountCurrencyOperations.LockAccount(db, user, cancel);
        var (_, account, state) = await Load(db, user, now, cancel);
        var owner = await db.LagoonEconomyRecords.AsNoTracking().SingleOrDefaultAsync(p => p.Id == "refcode:" + code, cancel)
            ?? throw new ArgumentException("Код приглашения не найден.");
        if (owner.UserId == user) throw new InvalidOperationException("Нельзя пригласить себя.");
        if (state.Referrer != null)
        {
            if (state.Referrer == owner.UserId) return;
            throw new InvalidOperationException("Пригласивший уже закреплён за аккаунтом.");
        }
        var observedPlaytime = await db.PlayTime.Where(p => p.PlayerId == user && p.Tracker == "Overall")
            .Select(p => (TimeSpan?)p.TimeSpent).FirstOrDefaultAsync(cancel);
        if (state.PlayedTicks >= TimeSpan.FromHours(2).Ticks || observedPlaytime >= TimeSpan.FromHours(2) || now - state.FirstSeen > TimeSpan.FromDays(7))
            throw new InvalidOperationException("Код вводится до 2 часов игры и в первые 7 дней.");
        // Only eligible newcomers can create an edge. Walk existing edges as a second guard against cycles.
        var next = owner.UserId;
        var visited = new HashSet<Guid> { user };
        while (true)
        {
            if (!visited.Add(next)) throw new InvalidOperationException("Циклическое приглашение запрещено.");
            var parent = await db.LagoonEconomyRecords.AsNoTracking().SingleOrDefaultAsync(p => p.Id == Key(next), cancel);
            var referral = parent == null ? null : Read<LagoonEconomyState>(parent).Referrer;
            if (referral == null) break;
            next = referral.Value;
        }
        state.Referrer = owner.UserId; Save(account, state);
        await db.SaveChangesAsync(cancel); beforeCommit?.Invoke(); await tx.CommitAsync(cancel);
    }
    public static async Task<List<(Guid User, LagoonCoinResult Result)>> ReferralRewards(ServerDbContext db, Guid user, DateTime now, CancellationToken cancel = default, Action? beforeCommit = null)
    {
        var initial = await db.LagoonEconomyRecords.AsNoTracking().SingleOrDefaultAsync(p => p.Id == Key(user), cancel);
        var inviter = initial == null ? null : Read<LagoonEconomyState>(initial).Referrer;
        if (inviter == null) return [];
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await AccountCurrencyOperations.LockAccount(db, user.CompareTo(inviter.Value) < 0 ? user : inviter.Value, cancel);
        await AccountCurrencyOperations.LockAccount(db, user.CompareTo(inviter.Value) < 0 ? inviter.Value : user, cancel);
        var (wallet, account, state) = await Load(db, user, now, cancel);
        var (ownerWallet, ownerRecord, ownerState) = await Load(db, inviter.Value, now, cancel);
        var days = state.QualifiedDayCount;
        var gained = 0; var ownerGained = 0;
        if (state.ReferralStage == 0 && state.PlayedTicks >= TimeSpan.FromHours(3).Ticks && days >= 2)
        {
            var month = LagoonEconomyRules.MoscowDay(now)[..7];
            state.ReferrerReward = ownerState.ReferralMonths.GetValueOrDefault(month) < 5;
            if (state.ReferrerReward) ownerState.ReferralMonths[month] = ownerState.ReferralMonths.GetValueOrDefault(month) + 1;
            state.ReferralStage = 1; gained += 15;
            Credit(db, wallet, "referral:first", 15, "referral-newcomer:first", now);
            if (state.ReferrerReward) { ownerGained += 25; Credit(db, ownerWallet, "referral:" + user.ToString("N") + ":first", 25, "referral-inviter:first", now); }
        }
        if (state.ReferralStage == 1 && state.PlayedTicks >= TimeSpan.FromHours(12).Ticks && days >= 5 && now - state.FirstSeen >= TimeSpan.FromDays(7))
        {
            state.ReferralStage = 2; gained += 35;
            Credit(db, wallet, "referral:return", 35, "referral-newcomer:return", now);
            if (state.ReferrerReward) { ownerGained += 75; Credit(db, ownerWallet, "referral:" + user.ToString("N") + ":return", 75, "referral-inviter:return", now); }
        }
        Save(account, state); Save(ownerRecord, ownerState);
        await db.SaveChangesAsync(cancel); beforeCommit?.Invoke(); await tx.CommitAsync(cancel);
        var results = new List<(Guid, LagoonCoinResult)>();
        if (gained > 0) results.Add((user, new(wallet.LagoonCoins, gained, true)));
        if (ownerGained > 0) results.Add((inviter.Value, new(ownerWallet.LagoonCoins, ownerGained, true)));
        return results;
    }
}

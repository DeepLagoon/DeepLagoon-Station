using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared.Database;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public static partial class AccountEconomyOperations
{
    public static async Task<LagoonCoinResult> Onboarding(ServerDbContext db, Guid user, Guid approver, Guid newcomer,
        string stage, string evidence, DateTime now, CancellationToken cancel = default, Action? beforeCommit = null)
    {
        if (user == approver || user == newcomer || approver == newcomer) throw new InvalidOperationException("Приёмка требует независимого сотрудника и отдельного новичка.");
        if (evidence.Length is < 1 or > 400) throw new ArgumentException("Укажите тикет и результат работы.");
        var amount = stage switch { "interview" => 20, "training" => 40, "return" => 20, _ => throw new ArgumentException("Unknown onboarding stage.") };
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await AccountCurrencyOperations.LockAccount(db, user.CompareTo(newcomer) < 0 ? user : newcomer, cancel);
        await AccountCurrencyOperations.LockAccount(db, user.CompareTo(newcomer) < 0 ? newcomer : user, cancel);
        var (wallet, record, state) = await Load(db, user, now, cancel);
        var (_, _, novice) = await Load(db, newcomer, now, cancel);
        if (stage == "return" && (novice.PlayedTicks < TimeSpan.FromHours(5).Ticks || novice.QualifiedDayCount < 3))
            throw new InvalidOperationException("Возвращение подтверждается после 5 часов игры в 3 разных дня.");
        var key = "onboarding:" + newcomer.ToString("N") + ":" + stage;
        var existing = await db.LagoonEconomyRecords.SingleOrDefaultAsync(p => p.Id == key, cancel);
        if (existing != null)
        {
            if (existing.UserId != user) throw new InvalidOperationException("Этот этап новичка уже оплачен другому исполнителю.");
            return new(wallet.LagoonCoins, 0, false);
        }
        var local = now.AddHours(3);
        var week = ISOWeek.GetYear(local) + "-" + ISOWeek.GetWeekOfYear(local);
        if (!state.MentorWeeks.TryGetValue(week, out var players)) state.MentorWeeks[week] = players = new HashSet<Guid>();
        if (!players.Contains(newcomer) && players.Count >= 5) throw new InvalidOperationException("До 5 цепочек приёма на сотрудника в неделю.");
        players.Add(newcomer);
        db.LagoonEconomyRecords.Add(new LagoonEconomyRecord { Id = key, UserId = user,
            Data = Json(new { newcomer, stage, evidence, approver, at = now }) });
        Credit(db, wallet, key, amount, "onboarding:" + stage, now, approver);
        Save(record, state); await db.SaveChangesAsync(cancel); beforeCommit?.Invoke(); await tx.CommitAsync(cancel);
        return new(wallet.LagoonCoins, amount, true);
    }
}

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public readonly record struct ParkingCredit(int Character, int Balance);

public static class ParkingBankOperations
{
    private sealed record Receipt(int Slot, int Amount);
    /// <summary>Debit and receipt share the account transaction. A retry reads today's balance without another debit.</summary>
    public static async Task<int> Debit(ServerDbContext db, Guid user, int slot, int amount, Guid operation, CancellationToken cancel = default)
    {
        if (amount <= 0 || slot < 0 || operation == Guid.Empty) throw new ArgumentException("Invalid parking payment.");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await AccountCurrencyOperations.LockAccount(db, user, cancel);
        var id = $"parking-credit:{user:N}:{operation:N}";
        var old = await db.LagoonEconomyRecords.SingleOrDefaultAsync(p => p.Id == id, cancel);
        var profile = await db.Profile.SingleAsync(p => p.Preference.UserId == user && p.Slot == slot, cancel);
        if (old != null)
        {
            if (old.UserId != user || JsonSerializer.Deserialize<Receipt>(old.Data) != new Receipt(slot, amount))
                throw new InvalidOperationException("Parking payment key was reused with different data.");
        }
        else
        {
            if (profile.BankBalance < amount) throw new InvalidOperationException("Недостаточно кредитов для выкупа шаттла.");
            profile.BankBalance -= amount;
            db.LagoonEconomyRecords.Add(new LagoonEconomyRecord { Id = id, UserId = user, Data = JsonSerializer.Serialize(new Receipt(slot, amount)) });
            await db.SaveChangesAsync(cancel);
        }
        await tx.CommitAsync(cancel);
        return profile.BankBalance;
    }

    public static async Task<ParkingCredit> Credit(ServerDbContext db, Guid user, int slot, int amount, Guid operation, CancellationToken cancel = default)
    {
        if (amount <= 0 || operation == Guid.Empty)
            throw new ArgumentException("Invalid parking credit.");

        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await AccountCurrencyOperations.LockAccount(db, user, cancel);
        var id = $"parking-sale:{user:N}:{operation:N}";
        var old = await db.LagoonEconomyRecords.SingleOrDefaultAsync(p => p.Id == id, cancel);
        var receipt = old == null ? null : JsonSerializer.Deserialize<Receipt>(old.Data);
        if (old != null && (old.UserId != user || receipt?.Amount != amount))
            throw new InvalidOperationException("Parking credit key was reused with different data.");

        var profiles = db.Profile.Where(p => p.Preference.UserId == user);
        var targetSlot = receipt?.Slot ?? slot;
        var profile = await profiles.SingleOrDefaultAsync(p => p.Slot == targetSlot, cancel);
        if (profile == null && receipt == null)
            profile = await profiles.SingleOrDefaultAsync(p => p.Slot == p.Preference.SelectedCharacterSlot, cancel);
        if (profile == null)
            throw new InvalidOperationException("Parking credit has no bank account.");

        if (receipt == null)
        {
            profile.BankBalance = checked(profile.BankBalance + amount);
            db.LagoonEconomyRecords.Add(new LagoonEconomyRecord
            {
                Id = id,
                UserId = user,
                Data = JsonSerializer.Serialize(new Receipt(profile.Slot, amount)),
            });
            await db.SaveChangesAsync(cancel);
        }

        await tx.CommitAsync(cancel);
        return new ParkingCredit(profile.Slot, profile.BankBalance);
    }
}

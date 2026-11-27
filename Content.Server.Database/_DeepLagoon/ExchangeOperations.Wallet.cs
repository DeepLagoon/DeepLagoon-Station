using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public static partial class ExchangeOperations
{
    private sealed record WalletOwner(Guid User, int Profile, string BalanceKey = "", string Name = "");
    private sealed record PasswordRecord(byte[] Salt, byte[] Hash, Guid Version = default);
    private static string BalanceKey(WalletOwner owner) => owner.BalanceKey.Length > 0 ? owner.BalanceKey : $"exchange:wallet:{owner.User:N}:{owner.Profile}";
    private static PasswordRecord HashPassword(string password)
    {
        if (password.Length is < 6 or > 128) throw new InvalidOperationException("password_length");
        var salt = RandomNumberGenerator.GetBytes(32);
        return new(salt, Rfc2898DeriveBytes.Pbkdf2(password, salt, 120000, HashAlgorithmName.SHA256, 32), Guid.NewGuid());
    }
    public const string AuditPrefix = "exchange:audit:";

    private static async Task<string> Address(ServerDbContext db, Guid user, int profile, CancellationToken cancel)
    {
        var key = $"exchange:wallet-address:{user:N}:{profile}";
        var saved = db.LagoonEconomyRecords.Local.FirstOrDefault(r => r.Id == key)
            ?? await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == key, cancel);
        if (saved != null) return JsonSerializer.Deserialize<string>(saved.Data)!;
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        // All callers hold LockMarket. The primary key also enforces uniqueness across processes.
        for (var attempt = 0; attempt < 128; attempt++)
        {
            var address = new string(Enumerable.Range(0, 6).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
            var ownerKey = "exchange:address:" + address;
            if (db.LagoonEconomyRecords.Local.Any(r => r.Id == ownerKey) ||
                await db.LagoonEconomyRecords.AnyAsync(r => r.Id == ownerKey, cancel)) continue;
            db.LagoonEconomyRecords.Add(new() { Id = ownerKey, UserId = user, Data = JsonSerializer.Serialize(new WalletOwner(user, profile)) });
            db.LagoonEconomyRecords.Add(new() { Id = key, UserId = user, Data = JsonSerializer.Serialize(address) });
            return address;
        }
        throw new InvalidOperationException("unavailable");
    }

    private static async Task<(WalletOwner Owner, Profile Profile)> Recipient(ServerDbContext db, string address, CancellationToken cancel)
    {
        address = address.Trim().ToUpperInvariant();
        if (address.Length is < 1 or > 6 || address.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new InvalidOperationException("wallet_missing");
        var ownerKey = "exchange:address:" + address;
        var row = db.LagoonEconomyRecords.Local.FirstOrDefault(r => r.Id == ownerKey)
            ?? await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == ownerKey, cancel);
        if (row == null) throw new InvalidOperationException("wallet_missing");
        var owner = JsonSerializer.Deserialize<WalletOwner>(row.Data)!;
        var profile = await db.Profile.SingleOrDefaultAsync(p => p.Id == owner.Profile && p.Preference.UserId == owner.User, cancel);
        if (profile == null) throw new InvalidOperationException("wallet_missing");
        return (owner, profile);
    }

    private static async Task<ExchangeResult> Result(ServerDbContext db, Guid user, Profile profile, ExchangeMarket market,
        bool applied, string selected, CancellationToken cancel)
    {
        var primary = await Address(db, user, profile.Id, cancel);
        var address = selected.Length > 0 ? selected : primary;
        var (owner, walletProfile) = await Recipient(db, address, cancel);
        var (_, wallet) = await Wallet(db, user, profile.Id, cancel, address);
        var rows = await db.LagoonEconomyRecords.Where(r => r.UserId == user && r.Id.StartsWith("exchange:address:")).ToListAsync(cancel);
        var owned = rows.Concat(db.LagoonEconomyRecords.Local.Where(r => r.UserId == user && r.Id.StartsWith("exchange:address:")))
            .DistinctBy(r => r.Id).Where(r => JsonSerializer.Deserialize<WalletOwner>(r.Data)!.Profile == profile.Id).ToArray();
        var keys = owned.Select(r => "exchange:password:" + r.Id["exchange:address:".Length..]).ToArray();
        var passwords = (await db.LagoonEconomyRecords.Where(r => keys.Contains(r.Id)).Select(r => r.Id).ToListAsync(cancel))
            .Concat(db.LagoonEconomyRecords.Local.Where(r => keys.Contains(r.Id)).Select(r => r.Id)).ToHashSet();
        var selectedPassword = "exchange:password:" + address;
        var hasPassword = passwords.Contains(selectedPassword) || await db.LagoonEconomyRecords.AnyAsync(r => r.Id == selectedPassword, cancel);
        var wallets = owned.Select(r => new ExchangeWalletInfo(r.Id["exchange:address:".Length..],
            JsonSerializer.Deserialize<WalletOwner>(r.Data)!.Name is { Length: > 0 } name ? name : "1",
            !passwords.Contains("exchange:password:" + r.Id["exchange:address:".Length..])))
            .OrderBy(w => w.Address != primary).ThenBy(w => w.Name).ToArray();
        var historyPrefix = "exchange:withdraw:" + address + ":";
        var history = (await db.LagoonEconomyRecords.AsNoTracking().Where(r => r.Id.StartsWith(historyPrefix))
                .OrderByDescending(r => r.Id).Take(50).ToListAsync(cancel))
            .Concat(db.LagoonEconomyRecords.Local.Where(r => r.Id.StartsWith(historyPrefix))).DistinctBy(r => r.Id)
            .OrderByDescending(r => r.Id).Take(50).Select(r => JsonSerializer.Deserialize<ExchangeWithdrawal>(r.Data)!).ToArray();
        var passwordRow = db.LagoonEconomyRecords.Local.FirstOrDefault(r => r.Id == selectedPassword)
            ?? await db.LagoonEconomyRecords.AsNoTracking().SingleOrDefaultAsync(r => r.Id == selectedPassword, cancel);
        return new(profile.BankBalance, profile.Id, profile.Slot, market, wallet, applied)
        { Address = address, WalletVersion = passwordRow == null ? Guid.Empty : JsonSerializer.Deserialize<PasswordRecord>(passwordRow.Data)!.Version,
            NeedsPassword = !hasPassword, Wallets = wallets, WalletCharacter = walletProfile.CharacterName, Withdrawals = history };
    }

    /// <summary>Passwords are verified only on login, on the database worker, and are never returned or logged.</summary>
    public static async Task<string> Login(ServerDbContext db, Guid user, int slot, ExchangeWalletLogin request,
        CancellationToken cancel = default) => (await LoginAccess(db, user, slot, request, cancel)).Address;

    public static async Task<ExchangeWalletAccess> LoginAccess(ServerDbContext db, Guid user, int slot, ExchangeWalletLogin request,
        CancellationToken cancel = default)
    {
        if (request.Password == null || request.Password.Length is < 6 or > 128) throw new InvalidOperationException("password_length");
        var address = request.Address.Trim().ToUpperInvariant();
        PasswordRecord? created = null;
        var passwordKey = "exchange:password:" + address;
        var saved = request.Create ? null : await db.LagoonEconomyRecords.AsNoTracking().SingleOrDefaultAsync(r => r.Id == passwordKey, cancel);
        if (saved != null)
        {
            var record = JsonSerializer.Deserialize<PasswordRecord>(saved.Data)!;
            var computed = Rfc2898DeriveBytes.Pbkdf2(request.Password, record.Salt, 120000, HashAlgorithmName.SHA256, 32);
            if (!CryptographicOperations.FixedTimeEquals(computed, record.Hash)) throw new InvalidOperationException("bad_credentials");
        }
        else created = HashPassword(request.Password);
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await LockMarket(db, cancel);
        var profile = await db.Profile.SingleOrDefaultAsync(p => p.Preference.UserId == user && p.Slot == slot, cancel);
        if (profile?.Id != request.ProfileId) throw new InvalidOperationException("character_changed");
        if (saved != null && (await db.LagoonEconomyRecords.AsNoTracking().SingleAsync(r => r.Id == passwordKey, cancel)).Data != saved.Data)
            throw new InvalidOperationException("bad_credentials");
        if (request.Create)
        {
            var count = (await db.LagoonEconomyRecords.Where(r => r.UserId == user && r.Id.StartsWith("exchange:address:")).ToListAsync(cancel))
                .Count(r => JsonSerializer.Deserialize<WalletOwner>(r.Data)!.Profile == profile.Id);
            if (count >= 16) throw new InvalidOperationException("wallet_limit");
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            do { address = new string(Enumerable.Range(0, 6).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray()); }
            while (await db.LagoonEconomyRecords.AnyAsync(r => r.Id == "exchange:address:" + address, cancel));
            db.LagoonEconomyRecords.Add(new() { Id = "exchange:address:" + address, UserId = user,
                Data = JsonSerializer.Serialize(new WalletOwner(user, profile.Id, "exchange:extra-wallet:" + address, (count + 1).ToString())) });
            db.LagoonEconomyRecords.Add(new() { Id = "exchange:extra-wallet:" + address, UserId = user, Data = "{}" });
            passwordKey = "exchange:password:" + address;
        }
        else
        {
            var (owner, _) = await Recipient(db, address, cancel);
            if (created != null && (owner.User != user || owner.Profile != profile.Id)) throw new InvalidOperationException("bad_credentials");
        }
        if (created != null)
        {
            // Recheck under the market lock: concurrent first-login requests cannot overwrite a password.
            if (await db.LagoonEconomyRecords.AnyAsync(r => r.Id == passwordKey, cancel)) throw new InvalidOperationException("bad_credentials");
            db.LagoonEconomyRecords.Add(new() { Id = passwordKey, UserId = user, Data = JsonSerializer.Serialize(created) });
        }
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return new(address, created?.Version ?? JsonSerializer.Deserialize<PasswordRecord>(saved!.Data)!.Version);
    }

    public static async Task<ExchangeRecipient> FindRecipient(ServerDbContext db, string address, CancellationToken cancel = default)
    {
        var (_, profile) = await Recipient(db, address, cancel);
        return new(address.Trim().ToUpperInvariant(), profile.CharacterName, profile.Id);
    }

    private static async Task CheckWalletVersion(ServerDbContext db, string address, Guid? expected, CancellationToken cancel)
    {
        if (expected == null) return; // Trusted background credits and legacy internal callers do not open wallets.
        var row = await db.LagoonEconomyRecords.AsNoTracking().SingleOrDefaultAsync(r => r.Id == "exchange:password:" + address, cancel);
        if (row == null || JsonSerializer.Deserialize<PasswordRecord>(row.Data)!.Version != expected)
            throw new InvalidOperationException("wallet_locked");
    }

    public static async Task<Guid> ChangePassword(ServerDbContext db, Guid user, int slot, ExchangePasswordChange request,
        CancellationToken cancel = default)
    {
        if (request.CurrentPassword == null || request.CurrentPassword.Length is < 6 or > 128 || request.NewPassword == null)
            throw new InvalidOperationException("password_length");
        var key = "exchange:password:" + request.Address.Trim().ToUpperInvariant();
        var previous = await db.LagoonEconomyRecords.AsNoTracking().SingleOrDefaultAsync(r => r.Id == key, cancel);
        if (previous == null) throw new InvalidOperationException("bad_credentials");
        var record = JsonSerializer.Deserialize<PasswordRecord>(previous.Data)!;
        var computed = Rfc2898DeriveBytes.Pbkdf2(request.CurrentPassword, record.Salt, 120000, HashAlgorithmName.SHA256, 32);
        if (!CryptographicOperations.FixedTimeEquals(computed, record.Hash)) throw new InvalidOperationException("bad_credentials");
        var replacement = HashPassword(request.NewPassword);
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await LockMarket(db, cancel);
        if (!await db.Profile.AnyAsync(p => p.Preference.UserId == user && p.Slot == slot && p.Id == request.ProfileId, cancel))
            throw new InvalidOperationException("character_changed");
        await Recipient(db, request.Address, cancel);
        var row = await db.LagoonEconomyRecords.SingleAsync(r => r.Id == key, cancel);
        await db.Entry(row).ReloadAsync(cancel);
        if (row.Data != previous.Data) throw new InvalidOperationException("bad_credentials");
        row.Data = JsonSerializer.Serialize(replacement);
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return replacement.Version;
    }

    public static async Task<ExchangeResult> Transfer(ServerDbContext db, Guid user, ExchangeTransferRequest request, long now,
        CancellationToken cancel = default)
    {
        if (request.Operation == Guid.Empty || request.Units is < 1 or > ExchangeMarket.MaxOrderUnits ||
            !ExchangeMarket.Assets.Any(a => a.Id == request.Asset)) throw new InvalidOperationException("invalid_order");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await LockMarket(db, cancel);
        var (owner, recipient) = await Recipient(db, request.Address, cancel);
        foreach (var id in new[] { user, owner.User }.Distinct().OrderBy(id => id))
            await AccountCurrencyOperations.LockAccount(db, id, cancel);
        var profile = await db.Profile.SingleOrDefaultAsync(p => p.Preference.UserId == user && p.Slot == request.Slot, cancel);
        if (profile?.Id != request.ProfileId) throw new InvalidOperationException("character_changed");
        if (recipient.Id != request.RecipientProfileId) throw new InvalidOperationException("wallet_missing");
        var key = $"exchange:transfer:{user:N}:{request.Operation:N}";
        var fingerprint = JsonSerializer.Serialize(request);
        var previous = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == key, cancel);
        var (row, market) = await Market(db, now, cancel);
        var address = request.SenderAddress.Length > 0 ? request.SenderAddress : await Address(db, user, profile.Id, cancel);
        if (address == request.Address.Trim().ToUpperInvariant()) throw new InvalidOperationException("self_transfer");
        var (senderRow, wallet) = await Wallet(db, user, profile.Id, cancel, address);
        if (previous != null)
        {
            if (JsonSerializer.Deserialize<Receipt>(previous.Data)!.Fingerprint != fingerprint)
                throw new InvalidOperationException("operation_mismatch");
            return await Result(db, user, profile, market, false, address, cancel);
        }
        await CheckWalletVersion(db, address, request.WalletVersion, cancel);
        var (recipientRow, target) = await Wallet(db, owner.User, recipient.Id, cancel, request.Address.Trim().ToUpperInvariant());
        var before = wallet.GetValueOrDefault(request.Asset);
        var targetBefore = target.GetValueOrDefault(request.Asset);
        if (before < request.Units) throw new InvalidOperationException("insufficient_crypto");
        wallet[request.Asset] = before - request.Units;
        target[request.Asset] = checked(targetBefore + request.Units);
        senderRow.Data = JsonSerializer.Serialize(wallet);
        recipientRow.Data = JsonSerializer.Serialize(target);
        row.Data = JsonSerializer.Serialize(market);
        db.LagoonEconomyRecords.Add(new() { Id = key, UserId = user, Data = JsonSerializer.Serialize(new Receipt(fingerprint)) });
        Audit(db, user, profile, "crypto_transfer", 0,
            new(request.Operation, request.Asset, request.Units, address, before, wallet[request.Asset],
                RecipientAddress: request.Address.ToUpperInvariant(), RecipientBefore: targetBefore, RecipientAfter: target[request.Asset],
                WalletCharacter: (await Recipient(db, address, cancel)).Profile.CharacterName),
            owner.User, recipient);
        var result = await Result(db, user, profile, market, true, address, cancel);
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return result;
    }

    private static void Audit(ServerDbContext db, Guid user, Profile profile, string kind, long amount, ExchangeAudit details,
        Guid? recipient = null, Profile? target = null)
    {
        var id = BankFinancialOperation.Record(db, user, profile, amount, kind, recipientId: recipient, recipient: target);
        db.LagoonEconomyRecords.Add(new() { Id = AuditPrefix + id.ToString("N"), UserId = user, Data = JsonSerializer.Serialize(details) });
    }
}

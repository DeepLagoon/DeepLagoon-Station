using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public enum RecoveryLocation { Carried, Loose, Destroyed, Persistent, Issued }

/// <summary>Versioned entity state. Entity IDs are serialization-local; Identity is authoritative.</summary>
public sealed record RecoveryAsset(
    Guid Identity, long Revision, Guid User, int Profile, int YamlId, string Prototype,
    RecoveryLocation Location, Guid? Parent, string[] BodyPath, string Data,
    string Kind = "item", int Amount = 0, Guid Purchase = default, Guid? ContainerStore = null);

public sealed record RecoveryPurchase(Guid Identity, Guid User, int Profile, long Amount,
    Guid[] Products, string State = "paid", Guid BankTransaction = default);

public sealed record RecoveryRun(Guid Identity, string State, DateTime Heartbeat, int Round = 0);
public sealed record RecoveryIncident(RecoveryRun Run, RecoveryAsset[] Assets, RecoveryPurchase[] Purchases);
public sealed record RecoveryBoot(RecoveryIncident[] Incidents);
public sealed record RecoveryClaim(long Balance, RecoveryAsset[] Assets);
public sealed record RecoveryPayment(string Scope, Guid Run, Guid[] Products, long Total, Guid User, int Profile, long[]? UnitPrices = null);
public sealed record RecoveryRemoval(string Scope, Guid Run, Guid[] Items);

/// <summary>
/// Uses the existing transactional JSON-record table, with a disjoint namespace. No local files or
/// Discord delivery acknowledgements participate in recovery. Callers serialize writes through one worker.
/// </summary>
public static class CrashRecoveryOperations
{
    public static string Prefix(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Length > 48 || scope.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Recovery scope must be 1–48 ASCII letters, digits, '-' or '_'.", nameof(scope));
        return "crash-v1:" + scope + ":";
    }

    private static string RunKey(string scope, Guid run) => Prefix(scope) + "run:" + run.ToString("N");
    private static string AssetKey(string scope, Guid run, Guid item) => Prefix(scope) + run.ToString("N") + ":a:" + item.ToString("N");
    private static string PurchaseKey(string scope, Guid run, Guid purchase) => Prefix(scope) + run.ToString("N") + ":p:" + purchase.ToString("N");
    private static T Read<T>(LagoonEconomyRecord row) => JsonSerializer.Deserialize<T>(row.Data) ?? throw new InvalidOperationException("Invalid recovery record: " + row.Id);
    private static LagoonEconomyRecord Row<T>(string key, Guid user, T value) => new() { Id = key, UserId = user, Data = JsonSerializer.Serialize(value) };

    public static RecoveryAsset[] Normalize(RecoveryAsset[] assets)
    {
        var byId = assets.ToDictionary(a => a.Identity);
        return assets.Select(asset =>
        {
            var root = asset;
            var seen = new HashSet<Guid> { asset.Identity };
            while (root.Parent is { } parent && byId.TryGetValue(parent, out var next))
            {
                if (!seen.Add(parent)) throw new InvalidOperationException("Cycle in recovery containment.");
                root = next;
            }
            if (root.Identity == asset.Identity || asset.Location is RecoveryLocation.Destroyed or RecoveryLocation.Persistent or RecoveryLocation.Issued) return asset;
            return asset with { User = root.User, Profile = root.Profile, Location = root.Location };
        }).ToArray();
    }

    public static void RecordPayment(ServerDbContext db, RecoveryPayment payment, Guid user, Profile profile, Guid transaction)
    {
        if (payment.User != user || payment.Profile != 0 && payment.Profile != profile.Id || payment.Total <= 0 || payment.Products.Length is < 1 or > 8192)
            throw new InvalidOperationException("Invalid recovery purchase attribution.");
        if (payment.UnitPrices != null && (payment.UnitPrices.Length != payment.Products.Length || payment.UnitPrices.Any(p => p < 0) || payment.UnitPrices.Sum(p => (long)p) != payment.Total))
            throw new InvalidOperationException("Recovery purchase allocation must equal the actual payment.");
        for (var i = 0; i < payment.Products.Length; i++)
        {
            var amount = payment.UnitPrices?[i] ?? payment.Total / payment.Products.Length + (i < payment.Total % payment.Products.Length ? 1 : 0);
            if (amount == 0) continue;
            var id = payment.Products[i];
            db.LagoonEconomyRecords.Add(Row(PurchaseKey(payment.Scope, payment.Run, id), user,
                new RecoveryPurchase(id, user, profile.Id, amount, [id], BankTransaction: transaction)));
        }
    }

    public static async Task RemoveInTransaction(ServerDbContext db, RecoveryRemoval removal, CancellationToken cancel)
    {
        var keys = removal.Items.Select(id => AssetKey(removal.Scope, removal.Run, id)).ToArray();
        var rows = await db.LagoonEconomyRecords.Where(r => keys.Contains(r.Id)).ToListAsync(cancel);
        foreach (var row in rows)
        {
            var asset = Read<RecoveryAsset>(row);
            row.Data = JsonSerializer.Serialize(asset with { Location = RecoveryLocation.Destroyed, Revision = long.MaxValue });
        }
        // Tombstones also cover stacks first registered immediately before depositing, whose first
        // background snapshot has not committed yet.
        foreach (var key in keys.Except(rows.Select(r => r.Id)))
        {
            var id = Guid.ParseExact(key[(key.LastIndexOf(':') + 1)..], "N");
            db.LagoonEconomyRecords.Add(Row(key, Guid.Empty, new RecoveryAsset(id, long.MaxValue, Guid.Empty, 0, 0, "",
                RecoveryLocation.Destroyed, null, [], "")));
        }
    }

    public static async Task<RecoveryBoot> Boot(ServerDbContext db, string scope, Guid run, DateTime now, CancellationToken cancel = default)
    {
        var prefix = Prefix(scope) + "run:";
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        var rows = await db.LagoonEconomyRecords.Where(r => r.Id.StartsWith(prefix)).ToListAsync(cancel);
        var incidents = new List<RecoveryIncident>();
        foreach (var row in rows)
        {
            var previous = Read<RecoveryRun>(row);
            if (previous.Identity == run)
                continue;
            if (previous.State == "open" && now - previous.Heartbeat < TimeSpan.FromSeconds(30))
                throw new InvalidOperationException("The previous recovery writer still holds its 30-second lease.");
            if (previous.State is "closed" or "settled")
            {
                if (now - previous.Heartbeat > TimeSpan.FromDays(7))
                {
                    var expired = Prefix(scope) + previous.Identity.ToString("N") + ":";
                    await db.LagoonEconomyRecords.Where(r => r.Id.StartsWith(expired)).ExecuteDeleteAsync(cancel);
                    db.LagoonEconomyRecords.Remove(row);
                }
                continue;
            }
            previous = previous with { State = "crashed" };
            row.Data = JsonSerializer.Serialize(previous);
            var dataPrefix = Prefix(scope) + previous.Identity.ToString("N") + ":";
            var data = await db.LagoonEconomyRecords.AsNoTracking().Where(r => r.Id.StartsWith(dataPrefix)).ToListAsync(cancel);
            var rawAssets = data.Where(r => r.Id.Contains(":a:")).Select(Read<RecoveryAsset>).ToArray();
            var rawPurchases = data.Where(r => r.Id.Contains(":p:")).Select(Read<RecoveryPurchase>).ToArray();
            var boxIds = rawAssets.Where(a => a.ContainerStore != null).Select(a => a.ContainerStore!.Value)
                .Concat(rawPurchases.SelectMany(p => p.Products)).Distinct().ToArray();
            var boxes = boxIds.Length == 0 ? new Dictionary<Guid, bool>() : await db.WayfarerSafetyDepositBox.AsNoTracking()
                .Where(b => boxIds.Contains(b.BoxId)).ToDictionaryAsync(b => b.BoxId, b => b.LastWithdrawn == null, cancel);
            rawAssets = rawAssets.Select(a => a.ContainerStore is { } id
                ? !boxes.ContainsKey(id) ? a with { Location = RecoveryLocation.Destroyed }
                    : boxes[id] ? a with { Location = RecoveryLocation.Persistent } : a : a).ToArray();
            foreach (var product in rawPurchases.SelectMany(p => p.Products).Where(id => boxes.ContainsKey(id) && rawAssets.All(a => a.Identity != id)))
                rawAssets = rawAssets.Append(new RecoveryAsset(product, 0, Guid.Empty, 0, 0, "", RecoveryLocation.Persistent, null, [], "", "persistent-box")).ToArray();
            // Issued tombstones still prevent refunds of already restored purchases.
            // Consumers select Carried assets for materialization independently.
            var assets = Normalize(rawAssets);
            var purchases = rawPurchases;
            if (!assets.Any(a => a.Location == RecoveryLocation.Carried) && !purchases.Any(p => Refundable(p, assets)))
            {
                row.Data = JsonSerializer.Serialize(previous with { State = "settled" });
                continue;
            }
            incidents.Add(new(previous, assets, purchases));
        }
        if (!rows.Any(r => Read<RecoveryRun>(r).Identity == run))
            db.LagoonEconomyRecords.Add(Row(RunKey(scope, run), Guid.Empty, new RecoveryRun(run, "open", now)));
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return new(incidents.ToArray());
    }

    public static bool Refundable(RecoveryPurchase purchase, RecoveryAsset[] assets)
        => purchase.State == "paid" && purchase.Amount > 0
            && !assets.Any(a => a.Purchase == purchase.Identity && a.Location != RecoveryLocation.Loose)
            && purchase.Products.All(id => !assets.Any(a => a.Identity == id && a.Location != RecoveryLocation.Loose));

    public static async Task Write(ServerDbContext db, string scope, Guid run, RecoveryAsset[] assets,
        RecoveryPurchase[] purchases, DateTime now, CancellationToken cancel = default)
    {
        if (assets.Length > 64 || purchases.Length > 64)
            throw new ArgumentException("Recovery writes must be bounded.");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        var head = await db.LagoonEconomyRecords.SingleAsync(r => r.Id == RunKey(scope, run), cancel);
        var state = Read<RecoveryRun>(head);
        if (state.State != "open")
            throw new InvalidOperationException("A closed recovery round cannot accept writes.");
        head.Data = JsonSerializer.Serialize(state with { Heartbeat = now });
        var keys = assets.Select(a => AssetKey(scope, run, a.Identity))
            .Concat(purchases.Select(p => PurchaseKey(scope, run, p.Identity))).ToArray();
        var existing = await db.LagoonEconomyRecords.Where(r => keys.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancel);
        foreach (var asset in assets)
        {
            var key = AssetKey(scope, run, asset.Identity);
            if (existing.TryGetValue(key, out var row))
            {
                var old = Read<RecoveryAsset>(row);
                if (old.Revision >= asset.Revision)
                    continue;
                // A location-only mutation must not erase the last good component snapshot.
                row.UserId = asset.User;
                row.Data = JsonSerializer.Serialize(asset.Data.Length == 0 ? asset with { Data = old.Data, Prototype = old.Prototype, YamlId = old.YamlId } : asset);
            }
            else db.LagoonEconomyRecords.Add(Row(key, asset.User, asset));
        }
        foreach (var purchase in purchases)
        {
            var key = PurchaseKey(scope, run, purchase.Identity);
            if (existing.TryGetValue(key, out var row)) row.Data = JsonSerializer.Serialize(purchase);
            else db.LagoonEconomyRecords.Add(Row(key, purchase.User, purchase));
        }
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
    }

    public static async Task Close(ServerDbContext db, string scope, Guid run, DateTime now, CancellationToken cancel = default)
    {
        var key = RunKey(scope, run);
        var row = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == key, cancel);
        if (row == null) return;
        var state = Read<RecoveryRun>(row);
        row.Data = JsonSerializer.Serialize(state with { State = "closed", Heartbeat = now });
        await db.SaveChangesAsync(cancel);
    }

    public static async Task<int?> ProfileId(ServerDbContext db, Guid user, int slot, CancellationToken cancel = default)
        => await db.Profile.Where(p => p.Preference.UserId == user && p.Slot == slot).Select(p => (int?)p.Id).SingleOrDefaultAsync(cancel);

    public static async Task<RecoveryIncident> ReadIncident(ServerDbContext db, string scope, Guid run, CancellationToken cancel = default)
    {
        var key = RunKey(scope, run);
        var head = await db.LagoonEconomyRecords.AsNoTracking().SingleAsync(r => r.Id == key, cancel);
        var prefix = Prefix(scope) + run.ToString("N") + ":";
        var records = await db.LagoonEconomyRecords.AsNoTracking().Where(r => r.Id.StartsWith(prefix)).ToListAsync(cancel);
        return new(Read<RecoveryRun>(head), records.Where(r => r.Id.Contains(":a:")).Select(Read<RecoveryAsset>).ToArray(),
            records.Where(r => r.Id.Contains(":p:")).Select(Read<RecoveryPurchase>).ToArray());
    }

    /// <summary>Transfer entitlements before materialization. A second crash recovers from the new run.</summary>
    public static async Task<RecoveryClaim> Claim(ServerDbContext db, string scope, Guid incident, Guid destination,
        Guid user, int profile, Guid[] assets, Guid[] refunds, CancellationToken cancel = default, int round = 0)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await AccountCurrencyOperations.LockAccount(db, user, cancel);
        var character = await db.Profile.SingleAsync(p => p.Id == profile && p.Preference.UserId == user, cancel);
        var runRow = await db.LagoonEconomyRecords.SingleAsync(r => r.Id == RunKey(scope, incident), cancel);
        if (Read<RecoveryRun>(runRow).State != "crashed") throw new InvalidOperationException("Only crashed rounds can be recovered.");
        var targetRun = await db.LagoonEconomyRecords.SingleAsync(r => r.Id == RunKey(scope, destination), cancel);
        if (Read<RecoveryRun>(targetRun).State != "open") throw new InvalidOperationException("Recovery destination is not open.");
        var claimed = new List<RecoveryAsset>();
        var assetPrefix = Prefix(scope) + incident.ToString("N") + ":a:";
        var assetRows = await db.LagoonEconomyRecords.Where(r => r.Id.StartsWith(assetPrefix)).ToListAsync(cancel);
        var effective = Normalize(assetRows.Select(Read<RecoveryAsset>).ToArray()).ToDictionary(a => a.Identity);
        foreach (var id in assets.Distinct())
        {
            var row = await db.LagoonEconomyRecords.SingleAsync(r => r.Id == AssetKey(scope, incident, id), cancel);
            var asset = effective[id];
            if (asset.User != user || asset.Profile != profile) throw new InvalidOperationException("Recovery owner mismatch.");
            if (asset.Location == RecoveryLocation.Issued)
            {
                var priorTarget = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == AssetKey(scope, destination, id), cancel);
                if (priorTarget != null) claimed.Add(Read<RecoveryAsset>(priorTarget));
                continue;
            }
            if (asset.Location != RecoveryLocation.Carried || asset.Data.Length == 0 && asset.Kind == "item")
                throw new InvalidOperationException("Asset is not recoverable.");
            var targetKey = AssetKey(scope, destination, id);
            if (!await db.LagoonEconomyRecords.AnyAsync(r => r.Id == targetKey, cancel))
                db.LagoonEconomyRecords.Add(Row(targetKey, user, asset with { Revision = 1 }));
            row.Data = JsonSerializer.Serialize(asset with { Location = RecoveryLocation.Issued });
            if (asset.ContainerStore is { } boxId)
            {
                var box = await db.WayfarerSafetyDepositBox.SingleAsync(b => b.BoxId == boxId, cancel);
                if (box.LastWithdrawn == null) throw new InvalidOperationException("This box is already in persistent storage.");
                box.LastWithdrawn = DateTime.UtcNow;
                box.LastWithdrawnRoundId = round;
            }
            claimed.Add(asset);
        }
        foreach (var id in refunds.Distinct())
        {
            var row = await db.LagoonEconomyRecords.SingleAsync(r => r.Id == PurchaseKey(scope, incident, id), cancel);
            var purchase = Read<RecoveryPurchase>(row);
            if (purchase.User != user || purchase.Profile != profile) throw new InvalidOperationException("Refund owner mismatch.");
            if (purchase.State == "refunded") continue;
            if (purchase.State != "paid" || purchase.Amount <= 0) throw new InvalidOperationException("Purchase is not refundable.");
            if (effective.Values.Any(a => a.Purchase == purchase.Identity && a.Location != RecoveryLocation.Loose))
                throw new InvalidOperationException("This purchase already has restored or consumed products.");
            // Examine all products, including those handed to another player or persisted in parking/apartments.
            foreach (var product in purchase.Products)
            {
                if (await db.WayfarerSafetyDepositBox.AnyAsync(b => b.BoxId == product, cancel))
                    throw new InvalidOperationException("This purchase already has a persistent deposit box.");
                var productRow = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == AssetKey(scope, incident, product), cancel);
                if (productRow == null) continue;
                var productState = Read<RecoveryAsset>(productRow);
                if (productState.Location != RecoveryLocation.Loose)
                    throw new InvalidOperationException("A restored, persisted, consumed or sold purchase must not be refunded.");
            }
            character.BankBalance = checked(character.BankBalance + purchase.Amount);
            BankFinancialOperation.Record(db, user, character, purchase.Amount, "bank_crash_refund");
            row.Data = JsonSerializer.Serialize(purchase with { State = "refunded" });
        }
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return new(character.BankBalance, claimed.ToArray());
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
namespace Content.Server.Database;

public sealed record VoyageMarketOffer(string Id, string Type, int Level, int Cost);
public sealed class VoyagePressure
{
    public double Base { get; set; }
    public double Last { get; set; }
    public long At { get; set; }
    public long QuietUntil { get; set; }
    public double Value(long now)
    {
        if (now < QuietUntil) return 0;
        var elapsed = Math.Max(0, now - Math.Max(At, QuietUntil));
        return Math.Clamp(Base + (Last - Base) * Math.Pow(.5, elapsed / 1800000d), 0, 1);
    }
}
public sealed class VoyageWorld
{
    public Guid Cycle { get; set; }
    public int Seed { get; set; }
    public bool Closed { get; set; }
    public long NextRaid { get; set; }
    public long NextResearchRefresh { get; set; }
    public long ResearchRefreshInterval { get; set; }
    public HashSet<string> Known { get; set; } = new();
    public Dictionary<string, VoyagePressure> Routes { get; set; } = new();
    public Dictionary<string, SectorResearchProgress> Research { get; set; } = new();
    public List<VoyageMarketOffer> Offers { get; set; } = new();
    public static int BaseCost(string type) => type switch { "cargo" => 6000, "survey" => 9000, "express" => 15000, "mercenary" => 25000, _ => throw new InvalidOperationException("Неизвестный экипаж") };
    public static int HirePrice(string type, int level) => (int)Math.Ceiling(BaseCost(type) * (1 + .30 * (level - 1) + .12 * (level - 1) * (level - 1)));
    public static int LevelFor(int xp) { var level = 1; while (level < 10 && xp >= level * (level + 1) * 30) level++; return level; }
    public static double Protection(int level, bool patrol) => (patrol ? .45 : 1) * Math.Max(.5, 1 - .05 * (level - 1));
    public double Risk(string[] stops, long now, int level, bool patrol)
    {
        var survival = 1d;
        for (var i = 1; i < stops.Length; i++)
        {
            var key = Key(stops[i - 1], stops[i]);
            var activity = Routes.TryGetValue(key, out var route) ? route.Value(now) : .5;
            survival *= 1 - Math.Clamp(activity * .06 * Protection(level, patrol), 0, .3);
        }
        return Math.Clamp(1 - survival, 0, .9);
    }
    public static string Key(string a, string b) => string.CompareOrdinal(a, b) < 0 ? a + ":" + b : b + ":" + a;
    private static double Depth(string key)
    {
        var depth = 0d;
        foreach (var node in key.Split(':'))
        {
            var parts = node.Split('-');
            if (parts.Length == 3 && parts[0] == "sector" && int.TryParse(parts[1], out var ring)) depth = Math.Max(depth, (ring + 1) / 8d);
        }
        return depth;
    }
    public void EnsureRoutes(string[] keys, long now)
    {
        foreach (var key in keys)
        {
            if (Routes.ContainsKey(key)) continue;
            var basis = .025 + .48 * Depth(key);
            Routes[key] = new() { Base = basis, Last = basis, At = now };
        }
        if (NextRaid == 0) NextRaid = now + 480000;
        if (now < NextRaid || Routes.Count == 0) return;
        // Bounded catch-up: a restart never rolls a week's worth of attacks in one tick.
        var candidates = Routes.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var selected = Routes[candidates[RandomNumberGenerator.GetInt32(candidates.Length)]];
        selected.Last = Math.Clamp(selected.Value(now) + .15 + RandomNumberGenerator.GetInt32(2500) / 10000d, 0, .95);
        selected.At = now; selected.QuietUntil = 0; NextRaid = now + 480000;
    }
    public int Combat(string[] stops, int level, long now, double savedRoll = -1)
    {
        var roll = savedRoll >= 0 ? savedRoll : RandomNumberGenerator.GetInt32(1000000) / 1000000d;
        var none = Math.Max(.15, .30 - .015 * (level - 1));
        var full = Math.Min(.4, .20 + .02 * (level - 1));
        var outcome = roll < none ? 0 : roll >= 1 - full ? 2 : 1;
        ApplyCombat(stops, outcome, now); return outcome;
    }
    public void ApplyCombat(string[] stops, int outcome, long now)
    {
        if (outcome == 0) return;
        for (var i = 1; i < stops.Length; i++)
            if (Routes.TryGetValue(Key(stops[i - 1], stops[i]), out var route))
            {
                route.Last = outcome == 2 ? 0 : route.Value(now) * .35;
                route.At = now; route.QuietUntil = outcome == 2 ? now + 1800000 : 0;
            }
    }
    public static VoyageMarketOffer NewOffer(string type, int? forceLevel = null)
    {
        var roll = RandomNumberGenerator.GetInt32(100);
        var level = forceLevel ?? (roll < 45 ? 1 : roll < 75 ? 2 : roll < 91 ? 3 : roll < 98 ? 4 : 5);
        return new(Guid.NewGuid().ToString("N"), type, level, HirePrice(type, level));
    }
}
public static partial class EconomyOperations
{
    private const string VoyageWorldId = "voyage:world:v2";
    private static async Task<(LagoonEconomyRecord Row, VoyageWorld State)> VoyageUniverse(ServerDbContext db, CancellationToken cancel)
        => await Load<VoyageWorld>(db, VoyageWorldId, Guid.Empty, cancel);
    public static async Task<VoyageWorld> ReadVoyageWorld(ServerDbContext db, bool allowNew, string[] routes, long now, CancellationToken cancel = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancel); await Lock(db, cancel);
        var (row, world) = await VoyageUniverse(db, cancel);
        if (world.Cycle == Guid.Empty || world.Closed && allowNew)
        {
            world = new() { Cycle = Guid.NewGuid(), Seed = RandomNumberGenerator.GetInt32(1, int.MaxValue) };
            foreach (var type in new[] { "cargo", "survey", "express", "mercenary" })
                for (var i = 0; i < 3; i++) world.Offers.Add(VoyageWorld.NewOffer(type, i == 0 ? 1 : null));
        }
        if (!world.Closed) world.EnsureRoutes(routes, now);
        row.Data = JsonSerializer.Serialize(world); await db.SaveChangesAsync(cancel); await tx.CommitAsync(cancel); return world;
    }
    public static async Task CloseVoyageWorld(ServerDbContext db, Guid cycle, CancellationToken cancel = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancel); await Lock(db, cancel);
        var (row, world) = await VoyageUniverse(db, cancel);
        if (cycle != Guid.Empty && world.Cycle != cycle) return;
        await CloseSectorResearch(db, world.Cycle, cancel);
        world.Closed = true; world.Known.Clear(); world.Routes.Clear(); world.Offers.Clear();
        row.Data = JsonSerializer.Serialize(world);
        await db.LagoonEconomyRecords.Where(r => r.Id.StartsWith("voyage:discovery:")).ExecuteDeleteAsync(cancel);
        await db.SaveChangesAsync(cancel); await tx.CommitAsync(cancel);
    }
    public static async Task<VoyageCrewRecord> HireVoyageMarketCrew(ServerDbContext db, Guid user, int profileId, int slot, string offerId, Guid operation, CancellationToken cancel = default)
    {
        if (operation == Guid.Empty || user == Guid.Empty) throw new InvalidOperationException("Некорректный найм");
        await using var tx = await db.Database.BeginTransactionAsync(cancel); await Lock(db, cancel); await LockUsers(db, [user], cancel);
        var key = VoyageCrewPrefix + operation.ToString("N");
        var old = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == key, cancel);
        if (old != null)
        {
            var prior = JsonSerializer.Deserialize<VoyageCrewRecord>(old.Data)!;
            if (prior.User != user || prior.Profile != profileId || prior.Slot != slot) throw new InvalidOperationException("Чужая операция найма");
            return prior;
        }
        var profile = await Profile(db, user, profileId, cancel);
        var (worldRow, world) = await VoyageUniverse(db, cancel);
        if (world.Closed) throw new InvalidOperationException("Раунд завершён");
        var offer = world.Offers.SingleOrDefault(o => o.Id == offerId) ?? throw new InvalidOperationException("Предложение найма уже изменилось");
        var existing = await ReadVoyages(db, user, profileId, cancel);
        if (existing.Crews.Length >= 16) throw new InvalidOperationException("Достигнут предел экипажей: 16");
        if (profile.Slot != slot || profile.BankBalance < offer.Cost) throw new InvalidOperationException("Недостаточно денег для найма");
        var crew = new VoyageCrewRecord(operation, user, profileId, slot, offer.Type, offer.Level, offer.Level * (offer.Level - 1) * 30, false, offer.Cost);
        profile.BankBalance -= offer.Cost; BankFinancialOperation.Record(db, user, profile, offer.Cost, "voyage_crew_hire");
        db.LagoonEconomyRecords.Add(new() { Id = key, UserId = user, Data = JsonSerializer.Serialize(crew) });
        world.Offers.Remove(offer);
        world.Offers.Add(VoyageWorld.NewOffer(offer.Type, world.Offers.Any(o => o.Type == offer.Type && o.Level == 1) ? null : 1));
        worldRow.Data = JsonSerializer.Serialize(world); await db.SaveChangesAsync(cancel); await tx.CommitAsync(cancel); return crew;
    }
}

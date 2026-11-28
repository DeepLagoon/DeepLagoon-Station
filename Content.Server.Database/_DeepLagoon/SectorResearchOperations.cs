using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
namespace Content.Server.Database;
public static partial class EconomyOperations
{
    public static async Task<SectorEncounterInfo> SectorEncounter(ServerDbContext db, Guid cycle, string sector, string[] route, double heat, long now, Guid encounter, int defeated, CancellationToken cancel = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancel); await Lock(db, cancel);
        var (row, world) = await VoyageUniverse(db, cancel);
        if (world.Cycle != cycle || world.Closed) return new(Guid.Empty, []);
        if (!world.Research.TryGetValue(sector, out var state)) world.Research[sector] = state = new();
        if (defeated >= 0)
        {
            if (state.Encounter == encounter && defeated < state.Pirates && state.DefeatedPirates.Add(defeated))
                world.ApplyCombat(route, state.DefeatedPirates.Count >= state.Pirates ? 2 : 1, now);
        }
        else if (state.Encounter == Guid.Empty || now >= state.NextEncounter && state.DefeatedPirates.Count >= state.Pirates)
        {
            state.Encounter = Guid.NewGuid(); state.DefeatedPirates.Clear(); state.NextEncounter = now + 1800000;
            var roll = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000000) / 1000000d;
            state.Pirates = roll < Math.Clamp(heat, 0, 1) ? Math.Clamp(1 + (int)(heat * 3), 1, 3) : 0;
        }
        row.Data = JsonSerializer.Serialize(world); await db.SaveChangesAsync(cancel); await tx.CommitAsync(cancel);
        return new(state.Encounter, Enumerable.Range(0, state.Pirates).Where(i => !state.DefeatedPirates.Contains(i)).ToArray());
    }
    public static async Task DiscoverSector(ServerDbContext db, Guid cycle, string sector, CancellationToken cancel = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancel); await Lock(db, cancel);
        var (row, world) = await VoyageUniverse(db, cancel);
        if (world.Cycle != cycle || world.Closed) return;
        world.Known.Add(sector); row.Data = JsonSerializer.Serialize(world); await db.SaveChangesAsync(cancel); await tx.CommitAsync(cancel);
    }
    private const string ResearchPrefix = "sector:research:", ServicePrefix = "sector:crew:", HirePrefix = "sector:hire:";
    private static async Task<T> SectorRecord<T>(ServerDbContext db, string prefix, Guid id, CancellationToken cancel) where T : class
        => JsonSerializer.Deserialize<T>((await db.LagoonEconomyRecords.SingleAsync(r => r.Id == prefix + id.ToString("N"), cancel)).Data)!;
    private static async Task SaveSectorRecord<T>(ServerDbContext db, string prefix, Guid id, Guid owner, T value, CancellationToken cancel)
    {
        var key = prefix + id.ToString("N"); var row = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == key, cancel);
        if (row == null) db.LagoonEconomyRecords.Add(new() { Id = key, UserId = owner, Data = JsonSerializer.Serialize(value) });
        else row.Data = JsonSerializer.Serialize(value);
    }
    private static async Task<T[]> SectorRecords<T>(ServerDbContext db, string prefix, CancellationToken cancel)
        => (await db.LagoonEconomyRecords.AsNoTracking().Where(r => r.Id.StartsWith(prefix)).Select(r => r.Data).ToArrayAsync(cancel))
            .Select(s => JsonSerializer.Deserialize<T>(s)!).ToArray();
    public static async Task<SectorResearchBoard> ReadSectorResearch(ServerDbContext db, Guid cycle, Guid user, int profile, CancellationToken cancel = default)
    {
        var hires = await SectorRecords<SectorCrewHire>(db, HirePrefix, cancel);
        return new((await SectorRecords<SectorResearchOrder>(db, ResearchPrefix, cancel)).Where(o => o.Cycle == cycle && !o.Closed && ResearchOrderEnabled(o)).ToArray(),
            (await SectorRecords<SectorCrewService>(db, ServicePrefix, cancel)).Where(c => c.Listed || c.User == user && c.Profile == profile).ToArray(),
            hires.Where(h => h.Buyer == user && h.BuyerProfile == profile || h.Provider == user && h.ProviderProfile == profile).TakeLast(64).ToArray(),
            hires.Where(h => h.Status < 3).Select(h => h.Offer).Distinct().ToArray());
    }
    // A finite game grant per sector/cycle, independent of station food and payroll budgets.
    // Reconciliation preserves active escrow/planet leases and never renews a completed grant.
    public static async Task<AutomaticSectorResearchRefresh> RefreshAutomaticSectorResearch(ServerDbContext db, Guid cycle,
        AutomaticSectorResearchTarget[] targets, long now, long refreshInterval = 900000, CancellationToken cancel = default)
    {
#if !DEBUG
        throw new InvalidOperationException("Независимый исследовательский фонд доступен только в Debug");
#else
        refreshInterval = Math.Clamp(refreshInterval, 1000, 86400000);
        if (targets.Length > 128 || targets.Select(t => t.Sector).Distinct().Count() != targets.Length ||
            targets.Any(t => string.IsNullOrWhiteSpace(t.Sector) || t.Sector.Length > 100 || t.Planets is < 1 or > 12 || t.Reward is < 100 or > 1000000))
            throw new InvalidOperationException("Некорректные цели исследования");
        await using var tx = await db.Database.BeginTransactionAsync(cancel); await Lock(db, cancel);
        var (row, world) = await VoyageUniverse(db, cancel);
        if (world.Closed || world.Cycle != cycle) return new(0, now + refreshInterval);
        var issued = (await SectorRecords<SectorResearchOrder>(db, ResearchPrefix, cancel))
            .Where(o => o.Cycle == cycle && o.DebugFunded).Select(o => o.Sector).ToHashSet();
        var created = 0;
        foreach (var target in targets)
        {
            if (issued.Contains(target.Sector)) continue;
            var paid = world.Research.TryGetValue(target.Sector, out var progress) ? progress.Completed.Where(i => i >= 0 && i < target.Planets).ToHashSet() : new();
            if (paid.Count == target.Planets) continue;
            var reserve = (target.Reward * (target.Planets - paid.Count) + target.Planets - 1) / target.Planets;
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("research-grant:" + cycle.ToString("N") + ":" + target.Sector));
            var id = new Guid(bytes.AsSpan(0, 16));
            var order = new SectorResearchOrder(id, cycle, target.Sector, "Исследовательский фонд Центкома", reserve, reserve,
                Guid.Empty, 0, 0, 0, target.Planets, paid, Automatic: true, DebugFunded: true);
            await SaveSectorRecord(db, ResearchPrefix, id, Guid.Empty, order, cancel); created++;
        }
        if (world.NextResearchRefresh <= now || world.ResearchRefreshInterval != refreshInterval) world.NextResearchRefresh = now + refreshInterval;
        world.ResearchRefreshInterval = refreshInterval;
        row.Data = JsonSerializer.Serialize(world); await db.SaveChangesAsync(cancel); await tx.CommitAsync(cancel);
        return new(created, world.NextResearchRefresh);
#endif
    }
    private static bool ResearchOrderEnabled(SectorResearchOrder order)
    {
#if DEBUG
        return true;
#else
        return !order.DebugFunded;
#endif
    }
    public static async Task<SectorResearchOrder> PublishSectorResearch(ServerDbContext db, SectorResearchOrder order, CancellationToken cancel = default)
    {
        if (order.Id == Guid.Empty || order.Total is < 100 or > 10000000 || order.Total != order.Remaining || order.Planets is < 1 or > 12 || order.Paid.Count != 0 || order.Closed)
            throw new InvalidOperationException("Некорректный заказ на исследование");
        await using var tx = await db.Database.BeginTransactionAsync(cancel); await Lock(db, cancel);
        var existing = await db.LagoonEconomyRecords.AsNoTracking().SingleOrDefaultAsync(r => r.Id == ResearchPrefix + order.Id.ToString("N"), cancel);
        if (existing != null)
        {
            var previous = JsonSerializer.Deserialize<SectorResearchOrder>(existing.Data)!;
            if (previous.User != order.User || previous.Profile != order.Profile || previous.Sector != order.Sector || previous.Total != order.Total || previous.Cycle != order.Cycle)
                throw new InvalidOperationException("Операция заказа уже использована");
            return previous;
        }
        var active = (await SectorRecords<SectorResearchOrder>(db, ResearchPrefix, cancel)).Where(o => !o.Closed && o.Cycle == order.Cycle).ToArray();
        if (active.Length >= 256 || order.User != Guid.Empty && active.Count(o => o.User == order.User && o.Profile == order.Profile) >= 8)
            throw new InvalidOperationException("Достигнут предел открытых заказов на исследование");
        var (_, world) = await VoyageUniverse(db, cancel);
        if (world.Closed || world.Cycle != order.Cycle) throw new InvalidOperationException("Раунд завершён");
        if (order.User != Guid.Empty)
        {
            await LockUsers(db, [order.User], cancel); var profile = await Profile(db, order.User, order.Profile, cancel);
            if (profile.Slot != order.Slot || profile.BankBalance < order.Total) throw new InvalidOperationException("Недостаточно денег для резервирования заказа");
            profile.BankBalance -= order.Total; BankFinancialOperation.Record(db, order.User, profile, order.Total, "sector_research_escrow");
        }
        // Already explored planets never become fresh sources of rewards when another order is posted.
        if (world.Research.TryGetValue(order.Sector, out var progress)) order = order with { Paid = progress.Completed.ToHashSet() };
        if (order.Paid.Count >= order.Planets) throw new InvalidOperationException("Сектор уже полностью исследован");
        await SaveSectorRecord(db, ResearchPrefix, order.Id, order.User, order, cancel);
        await db.SaveChangesAsync(cancel); await tx.CommitAsync(cancel); return order;
    }
    public static async Task CancelSectorResearch(ServerDbContext db, Guid id, Guid user, int profileId, CancellationToken cancel = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancel); await Lock(db, cancel); await LockUsers(db, [user], cancel);
        var order = await SectorRecord<SectorResearchOrder>(db, ResearchPrefix, id, cancel);
        if (order.User != user || order.Profile != profileId || user == Guid.Empty) throw new InvalidOperationException("Это чужой заказ");
        if (!order.Closed)
        {
            var profile = await Profile(db, user, profileId, cancel); profile.BankBalance = checked(profile.BankBalance + order.Remaining);
            BankFinancialOperation.Record(db, user, profile, order.Remaining, "sector_research_refund");
            await SaveSectorRecord(db, ResearchPrefix, id, user, order with { Remaining = 0, Closed = true }, cancel); await db.SaveChangesAsync(cancel);
        }
        await tx.CommitAsync(cancel);
    }
    public static async Task LeaseSectorPlanet(ServerDbContext db, Guid cycle, string sector, int planet, int count, Guid trip, Guid run, bool release, CancellationToken cancel = default)
    {
        if (planet < 0 || planet >= count || count is < 1 or > 12 || trip == Guid.Empty || run == Guid.Empty) throw new InvalidOperationException("Неизвестная планета");
        await using var tx = await db.Database.BeginTransactionAsync(cancel); await Lock(db, cancel);
        var (row, world) = await VoyageUniverse(db, cancel);
        if (world.Cycle != cycle || world.Closed) throw new InvalidOperationException("Раунд завершён");
        if (!world.Research.TryGetValue(sector, out var progress)) world.Research[sector] = progress = new();
        if (release)
        { if (progress.Leases.TryGetValue(planet, out var old) && old.Trip == trip) progress.Leases.Remove(planet); }
        else
        {
            if (progress.Completed.Contains(planet)) throw new InvalidOperationException("Планета уже исследована");
            if (progress.Leases.TryGetValue(planet, out var old) && old.Run == run && old.Trip != trip) throw new InvalidOperationException("На планете уже работает другой экипаж");
            progress.Leases[planet] = new(trip, run);
        }
        row.Data = JsonSerializer.Serialize(world); await db.SaveChangesAsync(cancel); await tx.CommitAsync(cancel);
    }
    public static async Task<long> CompleteSectorPlanet(ServerDbContext db, Guid cycle, string sector, int planet, Guid trip, Guid run, Guid user, int profileId, Guid crewOffer, int experience, CancellationToken cancel = default)
    {
        var result = await CompleteSectorPlanetTeams(db, cycle, sector, planet, trip, run, [new(user, profileId, crewOffer)], experience, cancel);
        return result.Sum(p => p.Amount);
    }
    /// <summary>One finite planet payment, atomically divided between unique successful captains.</summary>
    public static async Task<SectorResearchPayment[]> CompleteSectorPlanetTeams(ServerDbContext db, Guid cycle, string sector, int planet, Guid trip, Guid run, SectorResearchParticipant[] participants, int experience, CancellationToken cancel = default)
    {
        var teams = participants.DistinctBy(t => (t.User, t.Profile)).OrderBy(t => t.User).ThenBy(t => t.Profile).ToArray();
        if (teams.Length is < 1 or > 32) throw new InvalidOperationException("Некорректный состав исследовательских команд");
        await using var tx = await db.Database.BeginTransactionAsync(cancel); await Lock(db, cancel); await LockUsers(db, teams.Select(t => t.User).Distinct().ToArray(), cancel);
        var (row, world) = await VoyageUniverse(db, cancel);
        if (world.Cycle != cycle || world.Closed) return [];
        if (!world.Research.TryGetValue(sector, out var progress) || progress.Completed.Contains(planet)) return [];
        if (!progress.Leases.TryGetValue(planet, out var lease) || lease.Trip != trip || lease.Run != run) throw new InvalidOperationException("Исследование не принадлежит рейсу");
        long paid = 0;
        foreach (var order in (await SectorRecords<SectorResearchOrder>(db, ResearchPrefix, cancel)).Where(o => o.Cycle == cycle && o.Sector == sector && !o.Closed && ResearchOrderEnabled(o) && !o.Paid.Contains(planet)))
        {
            var remainingPlanets = order.Planets - order.Paid.Count;
            var share = Math.Min(order.Remaining, (order.Remaining + remainingPlanets - 1) / remainingPlanets);
            var marked = order.Paid.ToHashSet(); marked.Add(planet);
            await SaveSectorRecord(db, ResearchPrefix, order.Id, order.User, order with { Remaining = order.Remaining - share, Paid = marked, Closed = marked.Count >= order.Planets }, cancel);
            paid = checked(paid + share);
        }
        var payments = new List<SectorResearchPayment>();
        for (var i = 0; i < teams.Length; i++)
        {
            var team = teams[i]; var amount = paid / teams.Length + (i < paid % teams.Length ? 1 : 0);
            var profile = await Profile(db, team.User, team.Profile, cancel);
            if (amount > 0)
            {
                profile.BankBalance = checked(profile.BankBalance + amount);
                BankFinancialOperation.Record(db, team.User, profile, amount, "sector_research_reward");
            }
            payments.Add(new(team.User, team.Profile, amount));
            if (team.CrewOffer == Guid.Empty) continue;
            var service = (await SectorRecords<SectorCrewService>(db, ServicePrefix, cancel)).FirstOrDefault(c => c.Id == team.CrewOffer && c.User == team.User && c.Profile == team.Profile);
            if (service == null) continue;
            var xp = Math.Min(1000000, service.Experience + Math.Clamp(experience, 1, 100));
            await SaveSectorRecord(db, ServicePrefix, service.Id, team.User, service with { Experience = xp, Level = VoyageWorld.LevelFor(xp) }, cancel);
        }
        progress.Leases.Remove(planet); progress.Completed.Add(planet);
        row.Data = JsonSerializer.Serialize(world); await db.SaveChangesAsync(cancel); await tx.CommitAsync(cancel); return payments.ToArray();
    }
    public static async Task PublishSectorCrew(ServerDbContext db, SectorCrewService service, CancellationToken cancel = default)
    {
        if (service.Id == Guid.Empty || service.Price is < 100 or > 1000000 || service.Description.Length > 500 || service.Members is < 1 or > 64 || service.Weapons < 0)
            throw new InvalidOperationException("Некорректный профиль экипажа");
        await using var tx = await db.Database.BeginTransactionAsync(cancel); await Lock(db, cancel); await LockUsers(db, [service.User], cancel);
        var profile = await Profile(db, service.User, service.Profile, cancel); if (profile.Slot != service.Slot) throw new InvalidOperationException("Персонаж изменился");
        var old = (await SectorRecords<SectorCrewService>(db, ServicePrefix, cancel)).FirstOrDefault(c => c.Id == service.Id);
        if (old != null)
        {
            if (old.User != service.User || old.Profile != service.Profile) throw new InvalidOperationException("Профиль другого капитана");
            service = service with { Experience = old.Experience, Level = old.Level };
        }
        else service = service with { Experience = 0, Level = 1 };
        await SaveSectorRecord(db, ServicePrefix, service.Id, service.User, service, cancel); await db.SaveChangesAsync(cancel); await tx.CommitAsync(cancel);
    }
    public static async Task<SectorCrewHire> HireSectorCrew(ServerDbContext db, Guid id, Guid offerId, Guid buyer, int profileId, string name, string sector, long expectedPrice, CancellationToken cancel = default)
    {
        if (id == Guid.Empty) throw new InvalidOperationException("Некорректный договор");
        await using var tx = await db.Database.BeginTransactionAsync(cancel); await Lock(db, cancel); await LockUsers(db, [buyer], cancel);
        var old = (await SectorRecords<SectorCrewHire>(db, HirePrefix, cancel)).FirstOrDefault(h => h.Id == id);
        if (old != null)
        { if (old.Buyer != buyer || old.BuyerProfile != profileId || old.Offer != offerId) throw new InvalidOperationException("Операция найма уже использована"); return old; }
        var offer = await SectorRecord<SectorCrewService>(db, ServicePrefix, offerId, cancel);
        if (offer.Price != expectedPrice) throw new InvalidOperationException("Цена найма изменилась. Подтвердите новую стоимость");
        if (!offer.Listed || offer.User == buyer && offer.Profile == profileId) throw new InvalidOperationException("Экипаж недоступен для найма");
        var hires = await SectorRecords<SectorCrewHire>(db, HirePrefix, cancel);
        if (hires.Any(h => h.Offer == offerId && h.Status < 3) || hires.Count(h => h.Buyer == buyer && h.BuyerProfile == profileId && h.Status < 3) >= 8)
            throw new InvalidOperationException("Экипаж уже получил заказ или достигнут предел договоров");
        var profile = await Profile(db, buyer, profileId, cancel);
        if (profile.BankBalance < offer.Price) throw new InvalidOperationException("Недостаточно денег для найма");
        profile.BankBalance -= offer.Price; BankFinancialOperation.Record(db, buyer, profile, offer.Price, "sector_crew_escrow");
        var hire = new SectorCrewHire(id, offerId, buyer, profileId, profile.Slot, name, offer.User, offer.Profile, offer.Slot, offer.Captain, offer.Ship, offer.Price, sector);
        await SaveSectorRecord(db, HirePrefix, id, buyer, hire, cancel); await db.SaveChangesAsync(cancel); await tx.CommitAsync(cancel); return hire;
    }
    public static async Task<SectorCrewHire> SectorHireAction(ServerDbContext db, Guid id, Guid user, int profileId, string action, CancellationToken cancel = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancel); await Lock(db, cancel);
        var hire = await SectorRecord<SectorCrewHire>(db, HirePrefix, id, cancel);
        var provider = user == hire.Provider && profileId == hire.ProviderProfile;
        var buyer = user == hire.Buyer && profileId == hire.BuyerProfile;
        if (!provider && !buyer) throw new InvalidOperationException("Чужой договор");
        if (hire.Status >= 3) return hire;
        await LockUsers(db, [hire.Buyer, hire.Provider], cancel);
        var next = action switch
        {
            "accept" when provider && hire.Status == 0 => 1,
            "finish" when provider && hire.Status == 1 => 2,
            "confirm" when buyer && hire.Status == 2 => 3,
            "auto" when provider && hire.Status is 1 or 2 => 3,
            "decline" when provider => 4,
            "cancel" when buyer && hire.Status == 0 => 4,
            "fail" when provider => 5,
            _ => throw new InvalidOperationException("Действие недоступно для этого договора"),
        };
        if (next >= 3)
        {
            var recipient = next == 3 ? hire.Provider : hire.Buyer; var recipientProfile = next == 3 ? hire.ProviderProfile : hire.BuyerProfile;
            var profile = await Profile(db, recipient, recipientProfile, cancel); profile.BankBalance = checked(profile.BankBalance + hire.Price);
            BankFinancialOperation.Record(db, recipient, profile, hire.Price, next == 3 ? "sector_crew_reward" : "sector_crew_refund");
        }
        hire = hire with { Status = next }; await SaveSectorRecord(db, HirePrefix, id, hire.Buyer, hire, cancel);
        await db.SaveChangesAsync(cancel); await tx.CommitAsync(cancel); return hire;
    }
    private static async Task CloseSectorResearch(ServerDbContext db, Guid cycle, CancellationToken cancel)
    {
        foreach (var order in (await SectorRecords<SectorResearchOrder>(db, ResearchPrefix, cancel)).Where(o => o.Cycle == cycle && !o.Closed))
        {
            if (order.User != Guid.Empty && order.Remaining > 0)
            {
                await LockUsers(db, [order.User], cancel); var profile = await Profile(db, order.User, order.Profile, cancel);
                profile.BankBalance = checked(profile.BankBalance + order.Remaining); BankFinancialOperation.Record(db, order.User, profile, order.Remaining, "sector_research_refund");
            }
            await SaveSectorRecord(db, ResearchPrefix, order.Id, order.User, order with { Remaining = 0, Closed = true }, cancel);
        }
        // A round ending is not fulfilment of a human contract. Release all outstanding escrow to its payer.
        foreach (var hire in (await SectorRecords<SectorCrewHire>(db, HirePrefix, cancel)).Where(h => h.Status < 3))
        {
            await LockUsers(db, [hire.Buyer], cancel); var profile = await Profile(db, hire.Buyer, hire.BuyerProfile, cancel);
            profile.BankBalance = checked(profile.BankBalance + hire.Price); BankFinancialOperation.Record(db, hire.Buyer, profile, hire.Price, "sector_crew_refund");
            await SaveSectorRecord(db, HirePrefix, hire.Id, hire.Buyer, hire with { Status = 5 }, cancel);
        }
    }
}

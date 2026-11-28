using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record VoyageCrewRecord(Guid Id, Guid User, int Profile, int Slot, string Offer, int Level = 1, int Experience = 0, bool Lost = false, long HirePrice = 0);
public sealed record VoyageJob(Guid Id, Guid User, int Profile, int Slot, Guid Crew, string Order,
    string Destination, string Origin, string[] Stops, string Manifest, long Reward, byte Account,
    long Depart, long Arrive, long Return, bool Survey, bool Delivered = false, bool Paid = false,
    Guid Cycle = default, int Kind = 0, double OutRoll = -1, double BackRoll = -1, bool Failed = false,
    int Combat = -1, string Product = "", int Quantity = 0, long PurchaseCost = 0, bool CargoIssued = false, Guid CargoLease = default,
    bool ExperienceAwarded = false, int ExperienceGain = 0, Guid RefundOwner = default, double LegRisk = 0, double CombatRoll = -1,
    VoyageCargoLine[]? Cargo = null);
public sealed record VoyageCargoLine(string Prototype, string Name, int Quantity, long UnitCost);
public sealed record VoyageAccountState(long Bank, VoyageCrewRecord[] Crews, string[] Known, VoyageJob[] Parcels);

public static partial class EconomyOperations
{
    private const string VoyageCrewPrefix = "voyage:crew:", VoyageJobPrefix = "voyage:job:", VoyageActivePrefix = "voyage:active:";
    public static async Task<VoyageAccountState> ReadVoyages(ServerDbContext db, Guid user, int profileId, CancellationToken cancel = default)
    {
        var profile = await Profile(db, user, profileId, cancel);
        var data = await db.LagoonEconomyRecords.AsNoTracking().Where(r => r.UserId == user && r.Id.StartsWith(VoyageCrewPrefix))
            .Select(r => r.Data).ToArrayAsync(cancel);
        var worldJson = await db.LagoonEconomyRecords.AsNoTracking().Where(r => r.Id == VoyageWorldId).Select(r => r.Data).SingleOrDefaultAsync(cancel);
        var world = worldJson == null ? new VoyageWorld() : JsonSerializer.Deserialize<VoyageWorld>(worldJson)!;
        var parcelIds = await db.LagoonEconomyRecords.AsNoTracking().Where(r => r.UserId == user && r.Id.StartsWith("voyage:parcel:")).Select(r => r.Data).ToArrayAsync(cancel);
        var parcels = await db.LagoonEconomyRecords.AsNoTracking().Where(r => parcelIds.Contains(r.Id)).Select(r => r.Data).ToArrayAsync(cancel);
        return new(profile.BankBalance, data.Select(s => JsonSerializer.Deserialize<VoyageCrewRecord>(s)!).Where(c => c.Profile == profileId && !c.Lost).ToArray(),
            world.Closed ? [] : world.Known.ToArray(), parcels.Select(s => JsonSerializer.Deserialize<VoyageJob>(s)!).Where(j => j.Profile == profileId).ToArray());
    }
    public static async Task<VoyageJob[]> ActiveVoyages(ServerDbContext db, CancellationToken cancel = default)
    {
        var ids = await db.LagoonEconomyRecords.AsNoTracking().Where(r => r.Id.StartsWith(VoyageActivePrefix)).Select(r => r.Data).ToArrayAsync(cancel);
        var jobs = await db.LagoonEconomyRecords.AsNoTracking().Where(r => ids.Contains(r.Id)).Select(r => r.Data).ToArrayAsync(cancel);
        return jobs.Select(s => JsonSerializer.Deserialize<VoyageJob>(s)!).ToArray();
    }
    public static async Task HireVoyageCrew(ServerDbContext db, VoyageCrewRecord crew, long cost, CancellationToken cancel = default)
    {
        if (crew.Id == Guid.Empty || crew.User == Guid.Empty || cost is <= 0 or > 1000000) throw new InvalidOperationException("Некорректный найм");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await LockUsers(db, [crew.User], cancel);
        var profile = await Profile(db, crew.User, crew.Profile, cancel);
        var key = VoyageCrewPrefix + crew.Id.ToString("N");
        var old = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == key, cancel);
        if (old != null)
        {
            if (JsonSerializer.Deserialize<VoyageCrewRecord>(old.Data) != crew) throw new InvalidOperationException("Повторное использование операции");
        }
        else
        {
            var existing = await ReadVoyages(db, crew.User, crew.Profile, cancel);
            if (existing.Crews.Length >= 16) throw new InvalidOperationException("Достигнут предел экипажей: 16");
            if (profile.Slot != crew.Slot || profile.BankBalance < cost) throw new InvalidOperationException("Недостаточно денег на банковском счёте");
            profile.BankBalance -= cost;
            BankFinancialOperation.Record(db, crew.User, profile, cost, "voyage_crew_hire");
            db.LagoonEconomyRecords.Add(new() { Id = key, UserId = crew.User, Data = JsonSerializer.Serialize(crew) });
            await db.SaveChangesAsync(cancel);
        }
        await tx.CommitAsync(cancel);
    }
    public static async Task CommitVoyage(ServerDbContext db, VoyageJob job, RecoveryRemoval? removal, CancellationToken cancel = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await Lock(db, cancel); await LockUsers(db, [job.User], cancel);
        await CommitVoyageInTransaction(db, job, removal, cancel);
        await db.SaveChangesAsync(cancel); await tx.CommitAsync(cancel);
    }
    private static async Task CommitVoyageInTransaction(ServerDbContext db, VoyageJob job, RecoveryRemoval? removal, CancellationToken cancel)
    {
        if (job.Id == Guid.Empty || job.Reward < 0 || job.Reward > int.MaxValue || job.Depart >= job.Arrive || job.Arrive >= job.Return || job.Delivered || job.Paid)
            throw new InvalidOperationException("Некорректный рейс");
        var key = VoyageJobPrefix + job.Id.ToString("N");
        var previous = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == key, cancel);
        if (previous != null)
        {
            var old = JsonSerializer.Deserialize<VoyageJob>(previous.Data)!;
            if (old.User != job.User || old.Profile != job.Profile || old.Crew != job.Crew || old.Order != job.Order || old.Kind != job.Kind || old.Product != job.Product || old.Quantity != job.Quantity || old.PurchaseCost != job.PurchaseCost || old.Cycle != job.Cycle)
                throw new InvalidOperationException("Повторное использование рейса");
            return;
        }
        var profile = await Profile(db, job.User, job.Profile, cancel);
        var (_, world) = await VoyageUniverse(db, cancel);
        if (job.Cycle != Guid.Empty && (world.Cycle != job.Cycle || world.Closed)) throw new InvalidOperationException("Раунд завершён");
        if (profile.Slot != job.Slot) throw new InvalidOperationException("Персонаж изменился");
        var crewJson = await db.LagoonEconomyRecords.Where(r => r.Id == VoyageCrewPrefix + job.Crew.ToString("N")).Select(r => r.Data).SingleOrDefaultAsync(cancel);
        var crew = crewJson == null ? null : JsonSerializer.Deserialize<VoyageCrewRecord>(crewJson);
        if (crew == null || crew.Lost || crew.User != job.User || crew.Profile != job.Profile) throw new InvalidOperationException("Чужой экипаж");
        var active = await ActiveVoyages(db, cancel);
        if (active.Any(j => j.Crew == job.Crew)) throw new InvalidOperationException("Экипаж занят");
        if (active.Length >= 128 || active.Count(j => j.User == job.User && j.Profile == job.Profile) >= 8) throw new InvalidOperationException("Слишком много рейсов");
        if (job.Kind == 0 && !job.Survey && active.Any(j => j.Kind == 0 && !j.Survey && j.Order == job.Order)) throw new InvalidOperationException("Заказ уже принят");
        if (job.Kind == 2)
        {
            var cargo = job.Cargo ?? [new(job.Product, job.Manifest, job.Quantity, job.PurchaseCost / Math.Max(1, job.Quantity))];
            if (job.PurchaseCost <= 0 || cargo.Length is < 1 or > 32 || cargo.Any(l => l.Quantity is < 1 or > 5 || l.Prototype.Length == 0 || l.UnitCost < 1) ||
                cargo.Sum(l => l.Quantity) > 40 || cargo.Sum(l => checked(l.UnitCost * l.Quantity)) != job.PurchaseCost || profile.BankBalance < job.PurchaseCost)
                throw new InvalidOperationException("Недостаточно денег или некорректный состав закупки");
            if ((await ReadVoyages(db, job.User, job.Profile, cancel)).Parcels.Length >= 32) throw new InvalidOperationException("Сначала получите прибывший груз");
            profile.BankBalance -= job.PurchaseCost; BankFinancialOperation.Record(db, job.User, profile, job.PurchaseCost, "voyage_import");
        }
        if (job.OutRoll < 0) job = job with { OutRoll = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000000) / 1000000d, BackRoll = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000000) / 1000000d };
        if (job.CombatRoll < 0) job = job with { CombatRoll = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000000) / 1000000d };
        if (removal != null) await CrashRecoveryOperations.RemoveInTransaction(db, removal, cancel);
        db.LagoonEconomyRecords.Add(new() { Id = key, UserId = job.User, Data = JsonSerializer.Serialize(job) });
        db.LagoonEconomyRecords.Add(new() { Id = VoyageActivePrefix + job.Id.ToString("N"), UserId = job.User, Data = key });
        await NotifyCharacter(db, job.User, job.Profile, job.Id, "voyage-depart", "Рейс отправлен",
            $"{job.Manifest}. Направление: {job.Destination}." + (job.PurchaseCost > 0 ? $" Закупка: {job.PurchaseCost:N0} спессо." : $" Награда после возвращения: {job.Reward:N0} спессо."), -job.PurchaseCost, cancel);
    }
    public static async Task<VoyageJob> AdvanceVoyage(ServerDbContext db, Guid id, long now, CancellationToken cancel = default)
    {
        var key = VoyageJobPrefix + id.ToString("N");
        var user = await db.LagoonEconomyRecords.Where(r => r.Id == key).Select(r => r.UserId).SingleAsync(cancel);
        await using var tx = await db.Database.BeginTransactionAsync(cancel); await Lock(db, cancel); await LockUsers(db, [user], cancel);
        var row = await db.LagoonEconomyRecords.SingleAsync(r => r.Id == key, cancel);
        var job = JsonSerializer.Deserialize<VoyageJob>(row.Data)!;
        if (job.Paid) return job;
        var crewRow = await db.LagoonEconomyRecords.SingleAsync(r => r.Id == VoyageCrewPrefix + job.Crew.ToString("N"), cancel);
        var crew = JsonSerializer.Deserialize<VoyageCrewRecord>(crewRow.Data)!;
        var (worldRow, world) = await VoyageUniverse(db, cancel);
        var current = !world.Closed && (job.Cycle == Guid.Empty || job.Cycle == world.Cycle);
        double Risk() => current ? world.Risk(job.Stops, now, crew.Level, job.Kind == 3) : job.LegRisk;
        if (!job.Delivered && now >= job.Arrive)
        {
            var risk = Risk();
            var failed = job.Cycle != Guid.Empty && job.OutRoll >= 0 && job.OutRoll < risk;
            var combat = job.Kind == 3 && current ? world.Combat(job.Stops, crew.Level, now, job.CombatRoll) : -1;
            var gain = job.Kind == 3 ? (int)Math.Clamp(Math.Ceiling(risk * 800), 0, 100) : Math.Min(40, 5 + job.Stops.Length * 2);
            job = job with { Delivered = !failed, Failed = failed, Combat = combat, ExperienceGain = gain };
            if (!failed) await NotifyCharacter(db, job.User, job.Profile, job.Id, "voyage-delivered", "Рейс достиг цели",
                $"{job.Manifest}. Сектор: {job.Destination}. Экипаж возвращается.", 0, cancel);
            if (job.Survey && !failed && current) world.Known.Add(job.Destination);
        }
        if (job.Delivered && !job.Failed && now >= job.Return)
            job = job with { Failed = job.Cycle != Guid.Empty && job.BackRoll >= 0 && job.BackRoll < Risk() };
        if (job.Failed || job.Delivered && now >= job.Return)
        {
            if (job.Failed) crew = crew with { Lost = true };
            else
            {
                var profile = await Profile(db, job.User, job.Profile, cancel);
                if (job.Reward > 0)
                {
                    profile.BankBalance = checked(profile.BankBalance + job.Reward);
                    BankFinancialOperation.Record(db, job.User, profile, job.Reward, "voyage_reward");
                }
                if (!job.ExperienceAwarded)
                {
                    var xp = Math.Min(1000000, crew.Experience + job.ExperienceGain);
                    crew = crew with { Experience = xp, Level = Math.Max(crew.Level, VoyageWorld.LevelFor(xp)) };
                }
                if (job.Kind == 2)
                    db.LagoonEconomyRecords.Add(new() { Id = "voyage:parcel:" + id.ToString("N"), UserId = job.User, Data = key });
            }
            job = job with { Paid = true, ExperienceAwarded = !job.Failed };
            await NotifyCharacter(db, job.User, job.Profile, job.Id, "voyage-finished", job.Failed ? "Рейс потерян" : "Рейс вернулся",
                job.Failed ? $"{job.Manifest}. Груз и экипаж утрачены. Награда не выплачена." :
                    job.Kind == 2 ? $"{job.Manifest}. Закупленный груз готов к получению." : $"{job.Manifest}. Награда: {job.Reward:N0} спессо.", job.Failed ? 0 : job.Reward, cancel);
            crewRow.Data = JsonSerializer.Serialize(crew);
            var active = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == VoyageActivePrefix + id.ToString("N"), cancel);
            if (active != null) db.LagoonEconomyRecords.Remove(active);
        }
        worldRow.Data = JsonSerializer.Serialize(world); row.Data = JsonSerializer.Serialize(job);
        await db.SaveChangesAsync(cancel); await tx.CommitAsync(cancel); return job;
    }
    public static async Task<VoyageJob> IssueVoyageCargo(ServerDbContext db, Guid user, int profile, Guid id, Guid lease, RecoveryExternal? recovery, CancellationToken cancel = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancel); await Lock(db, cancel); await LockUsers(db, [user], cancel);
        var row = await db.LagoonEconomyRecords.SingleAsync(r => r.Id == VoyageJobPrefix + id.ToString("N"), cancel);
        var job = JsonSerializer.Deserialize<VoyageJob>(row.Data)!;
        if (job.User != user || job.Profile != profile || job.Kind != 2 || !job.Paid || job.Failed || job.CargoIssued && job.CargoLease != lease)
            throw new InvalidOperationException("Груз недоступен");
        if (!job.CargoIssued)
        {
            if (recovery != null) await CrashRecoveryOperations.ImportExternal(db, recovery, user, profile, cancel);
            job = job with { CargoIssued = true, CargoLease = lease }; row.Data = JsonSerializer.Serialize(job);
            var parcel = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == "voyage:parcel:" + id.ToString("N"), cancel);
            if (parcel != null) db.LagoonEconomyRecords.Remove(parcel);
            await db.SaveChangesAsync(cancel);
        }
        await tx.CommitAsync(cancel); return job;
    }
    public static async Task<VoyageJob> ClaimVoyageRefund(ServerDbContext db, Guid id, Guid lease, CancellationToken cancel = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancel); await Lock(db, cancel);
        var row = await db.LagoonEconomyRecords.SingleAsync(r => r.Id == VoyageJobPrefix + id.ToString("N"), cancel);
        var job = JsonSerializer.Deserialize<VoyageJob>(row.Data)!;
        if (job.Failed && job.Reward > 0 && job.RefundOwner == Guid.Empty)
        { job = job with { RefundOwner = lease }; row.Data = JsonSerializer.Serialize(job); await db.SaveChangesAsync(cancel); }
        await tx.CommitAsync(cancel); return job;
    }

}

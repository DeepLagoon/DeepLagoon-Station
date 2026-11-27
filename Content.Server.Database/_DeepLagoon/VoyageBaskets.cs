using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record VoyageBasketHire(VoyageCrewRecord Crew, string Offer, long Cost);
public sealed record VoyageBasketCommit(Guid Id, Guid User, int Profile, int Slot, VoyageJob[] Jobs, VoyageBasketHire[] Hires, RecoveryRemoval?[] Removals);

public static partial class EconomyOperations
{
    public static async Task CommitVoyageBasket(ServerDbContext db, VoyageBasketCommit batch, CancellationToken cancel = default)
    {
        if (batch.Id == Guid.Empty || batch.User == Guid.Empty || batch.Jobs.Length is < 1 or > 8 || batch.Removals.Length != batch.Jobs.Length ||
            batch.Hires.Length > batch.Jobs.Length || batch.Jobs.Select(j => j.Id).Distinct().Count() != batch.Jobs.Length ||
            batch.Jobs.Select(j => j.Crew).Distinct().Count() != batch.Jobs.Length || batch.Hires.Select(h => h.Offer).Distinct().Count() != batch.Hires.Length ||
            batch.Jobs.Any(j => j.User != batch.User || j.Profile != batch.Profile || j.Slot != batch.Slot) ||
            batch.Hires.Any(h => h.Crew.User != batch.User || h.Crew.Profile != batch.Profile || h.Crew.Slot != batch.Slot || !batch.Jobs.Any(j => j.Crew == h.Crew.Id)))
            throw new InvalidOperationException("Некорректная корзина рейсов");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await Lock(db, cancel); await LockUsers(db, [batch.User], cancel);
        var key = "voyage:basket:" + batch.Id.ToString("N");
        var fingerprint = Fingerprint(batch);
        var receipt = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == key, cancel);
        if (receipt != null)
        {
            if (receipt.Data != fingerprint) throw new InvalidOperationException("Повторное использование корзины");
            return;
        }
        var profile = await Profile(db, batch.User, batch.Profile, cancel);
        if (profile.Slot != batch.Slot) throw new InvalidOperationException("Персонаж изменился");
        var (worldRow, world) = await VoyageUniverse(db, cancel);
        if (world.Closed || batch.Jobs.Any(j => j.Cycle != world.Cycle)) throw new InvalidOperationException("Раунд завершён");
        var state = await ReadVoyages(db, batch.User, batch.Profile, cancel);
        if (state.Crews.Length + batch.Hires.Length > 16) throw new InvalidOperationException("Достигнут предел экипажей: 16");
        var hireCost = batch.Hires.Sum(h => h.Cost);
        var purchaseCost = batch.Jobs.Sum(j => j.PurchaseCost);
        if (hireCost < 0 || profile.BankBalance < checked(hireCost + purchaseCost)) throw new InvalidOperationException("Недостаточно денег на всю корзину и найм");
        foreach (var hire in batch.Hires)
        {
            var offer = world.Offers.SingleOrDefault(o => o.Id == hire.Offer);
            if (offer == null || offer.Cost != hire.Cost || hire.Crew.HirePrice != offer.Cost || offer.Type != hire.Crew.Offer || offer.Level != hire.Crew.Level || hire.Crew.Id == Guid.Empty)
                throw new InvalidOperationException("Предложение найма изменилось. Пересчитайте корзину");
            world.Offers.Remove(offer);
            world.Offers.Add(VoyageWorld.NewOffer(offer.Type, world.Offers.Any(o => o.Type == offer.Type && o.Level == 1) ? null : 1));
            db.LagoonEconomyRecords.Add(new() { Id = VoyageCrewPrefix + hire.Crew.Id.ToString("N"), UserId = batch.User, Data = JsonSerializer.Serialize(hire.Crew) });
            profile.BankBalance -= hire.Cost;
            BankFinancialOperation.Record(db, batch.User, profile, hire.Cost, "voyage_crew_hire");
            await NotifyCharacter(db, batch.User, batch.Profile, hire.Crew.Id, "crew-hire", "Экипаж нанят",
                $"Уровень: {hire.Crew.Level}. Стоимость найма: {hire.Cost:N0} спессо.", -hire.Cost, cancel);
        }
        worldRow.Data = JsonSerializer.Serialize(world);
        await db.SaveChangesAsync(cancel);
        for (var i = 0; i < batch.Jobs.Length; i++)
        {
            await CommitVoyageInTransaction(db, batch.Jobs[i], batch.Removals[i], cancel);
            await db.SaveChangesAsync(cancel);
        }
        db.LagoonEconomyRecords.Add(new() { Id = key, UserId = batch.User, Data = fingerprint });
        await db.SaveChangesAsync(cancel); await tx.CommitAsync(cancel);
    }
}

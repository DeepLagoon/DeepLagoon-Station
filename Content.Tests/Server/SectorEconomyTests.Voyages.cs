using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
namespace Content.Tests.Server;
public sealed partial class SectorEconomyTests
{
    private async Task<VoyageCrewRecord> HireCrew()
    {
        var crew = new VoyageCrewRecord(Guid.NewGuid(), _user, await ProfileId(_user), 0, "cargo");
        await using var db = new SqliteServerDbContext(_options);
        await EconomyOperations.HireVoyageCrew(db, crew, 6000); return crew;
    }
    private async Task<VoyageJob> AdvanceJob(Guid job, long now)
    { await using var db = new SqliteServerDbContext(_options); return await EconomyOperations.AdvanceVoyage(db, job, now); }
    private VoyageJob Job(VoyageCrewRecord crew) => new(Guid.NewGuid(), _user, crew.Profile, 0, crew.Id, "actual-order-instance", "helios", "Origin",
        ["colossus", "helios"], "3 burgers", 4500, 1, 1000, 61000, 121000, false);
    [Test]
    public async Task VoyageCrewHireDebitsExactlyOnceAndCannotSpendAnotherCharacterBalance()
    {
        var crew = await HireCrew();
        await using var db = new SqliteServerDbContext(_options);
        await EconomyOperations.HireVoyageCrew(db, crew, 6000);
        var state = await EconomyOperations.ReadVoyages(db, _user, crew.Profile);
        Assert.That(state.Bank, Is.EqualTo(794000)); Assert.That(state.Crews, Has.Length.EqualTo(1));
        Assert.That((await EconomyOperations.Read(db, _user, 1)).Bank, Is.EqualTo(500000));
        Assert.That(await db.BankFinancialOperations.CountAsync(r => r.Kind == "voyage_crew_hire"), Is.EqualTo(1));
    }
    [Test]
    public async Task VoyageCannotPayAtDispatchOrDeliveryAndPaysOnceOnlyAfterReturnEvenAfterReload()
    {
        var crew = await HireCrew(); var job = Job(crew);
        await using (var db = new SqliteServerDbContext(_options)) await EconomyOperations.CommitVoyage(db, job, null);
        Assert.That((await AdvanceJob(job.Id, 60999)).Delivered, Is.False);
        Assert.That((await AdvanceJob(job.Id, 61000)).Delivered, Is.True);
        Assert.That((await AdvanceJob(job.Id, 120999)).Paid, Is.False);
        await using (var db = new SqliteServerDbContext(_options))
        {
            Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(794000));
            Assert.That(await EconomyOperations.ActiveVoyages(db), Has.Length.EqualTo(1));
        }
        Assert.That((await AdvanceJob(job.Id, 121000)).Paid, Is.True);
        Assert.That((await AdvanceJob(job.Id, 999999)).Paid, Is.True);
        await using (var db = new SqliteServerDbContext(_options))
        {
            await EconomyOperations.CommitVoyage(db, job, null); // network/database retry cannot create a second delivery
            Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(798500));
            Assert.That((await EconomyOperations.Read(db, _user, 1)).Bank, Is.EqualTo(500000));
            Assert.That(await db.BankFinancialOperations.CountAsync(r => r.Kind == "voyage_reward"), Is.EqualTo(1));
            Assert.That(await EconomyOperations.ActiveVoyages(db), Is.Empty);
        }
    }
    [Test]
    public async Task VoyageRejectsUnhiredOrBusyCrewsAndUnaffordableHireWithoutCreatingMoney()
    {
        var crew = await HireCrew(); var job = Job(crew);
        await using (var db = new SqliteServerDbContext(_options)) await EconomyOperations.CommitVoyage(db, job, null);
        await using (var db = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<InvalidOperationException>(async () => await EconomyOperations.CommitVoyage(db, job with { Id = Guid.NewGuid() }, null));
        await using (var db = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<InvalidOperationException>(async () => await EconomyOperations.CommitVoyage(db, job with { Id = Guid.NewGuid(), Crew = Guid.NewGuid() }, null));
        await using (var db = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<InvalidOperationException>(async () => await EconomyOperations.HireVoyageCrew(db, crew with { Id = Guid.NewGuid() }, 900000));
        await using (var db = new SqliteServerDbContext(_options))
        {
            Assert.That((await EconomyOperations.ReadVoyages(db, _user, crew.Profile)).Crews, Has.Length.EqualTo(1));
            Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(794000));
        }
    }
}

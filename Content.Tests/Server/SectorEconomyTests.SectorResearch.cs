using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Voyages;
using NUnit.Framework;
namespace Content.Tests.Server;
public sealed partial class SectorEconomyTests
{
#if DEBUG
    [Test]
    public async Task AutomaticResearchFundsEveryAvailableSectorWithoutBankAccountsAndRefreshNeverRenewsEscrow()
    {
        var world = await ReadWorld();
        AutomaticSectorResearchTarget[] targets = [new("echo", 3, 6000), new("frontier", 4, 8000)];
        await using (var db = new SqliteServerDbContext(_options))
        {
            var result = await EconomyOperations.RefreshAutomaticSectorResearch(db, world.Cycle, targets, 1000);
            Assert.That(result.Created, Is.EqualTo(2)); Assert.That(result.NextRefresh, Is.EqualTo(901000));
            Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(800000));
        }
        Guid orderId;
        await using (var db = new SqliteServerDbContext(_options))
        {
            var board = await EconomyOperations.ReadSectorResearch(db, world.Cycle, _user, await ProfileId(_user));
            Assert.That(board.Orders.All(o => o.Automatic && o.User == Guid.Empty && o.Buyer == "Исследовательский фонд Центкома"), Is.True);
            orderId = board.Orders.Single(o => o.Sector == "echo").Id;
            Assert.That((await EconomyOperations.RefreshAutomaticSectorResearch(db, world.Cycle, targets, 2000)).NextRefresh, Is.EqualTo(901000));
            var trip = Guid.NewGuid(); var run = Guid.NewGuid();
            await EconomyOperations.LeaseSectorPlanet(db, world.Cycle, "echo", 0, 3, trip, run, false);
            Assert.That(await EconomyOperations.CompleteSectorPlanet(db, world.Cycle, "echo", 0, trip, run, _user, await ProfileId(_user), Guid.Empty, 20), Is.EqualTo(2000));
        }
        await using (var db = new SqliteServerDbContext(_options))
        {
            var result = await EconomyOperations.RefreshAutomaticSectorResearch(db, world.Cycle, targets, 901000);
            Assert.That(result.Created, Is.Zero); Assert.That(result.NextRefresh, Is.EqualTo(1801000));
            var order = (await EconomyOperations.ReadSectorResearch(db, world.Cycle, _user, await ProfileId(_user))).Orders.Single(o => o.Id == orderId);
            Assert.That(order.Remaining, Is.EqualTo(4000)); Assert.That(order.Paid, Is.EquivalentTo(new[] { 0 }));
            Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(802000));
        }
    }
    [Test]
    public async Task AutomaticResearchAddsNewFrontiersWithoutDelayingRefreshAndNeverPaysPreviouslyExploredPlanets()
    {
        var world = await ReadWorld(); var profile = await ProfileId(_user); var trip = Guid.NewGuid(); var run = Guid.NewGuid();
        await using var db = new SqliteServerDbContext(_options);
        await EconomyOperations.RefreshAutomaticSectorResearch(db, world.Cycle, [new("frontier", 4, 8000)], 1000);
        await EconomyOperations.LeaseSectorPlanet(db, world.Cycle, "echo", 0, 3, trip, run, false);
        Assert.That(await EconomyOperations.CompleteSectorPlanet(db, world.Cycle, "echo", 0, trip, run, _user, profile, Guid.Empty, 20), Is.Zero);
        var added = await EconomyOperations.RefreshAutomaticSectorResearch(db, world.Cycle, [new("frontier", 4, 8000), new("echo", 3, 6000)], 2000);
        Assert.That(added.Created, Is.EqualTo(1)); Assert.That(added.NextRefresh, Is.EqualTo(901000));
        var grant = (await EconomyOperations.ReadSectorResearch(db, world.Cycle, _user, profile)).Orders.Single(o => o.Sector == "echo");
        Assert.That(grant.Remaining, Is.EqualTo(4000)); Assert.That(grant.Paid, Does.Contain(0));
        for (var i = 1; i < 3; i++)
        {
            await EconomyOperations.LeaseSectorPlanet(db, world.Cycle, "echo", i, 3, trip, run, false);
            Assert.That(await EconomyOperations.CompleteSectorPlanet(db, world.Cycle, "echo", i, trip, run, _user, profile, Guid.Empty, 20), Is.EqualTo(2000));
        }
        Assert.That((await EconomyOperations.RefreshAutomaticSectorResearch(db, world.Cycle, [new("echo", 3, 6000)], 1801000)).Created, Is.Zero);
        Assert.That((await EconomyOperations.ReadSectorResearch(db, world.Cycle, _user, profile)).Orders.Any(o => o.Sector == "echo"), Is.False);
        Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(804000));
        await EconomyOperations.CloseVoyageWorld(db, world.Cycle);
        Assert.That((await EconomyOperations.RefreshAutomaticSectorResearch(db, world.Cycle, [new("echo", 3, 6000)], 2701000)).Created, Is.Zero);
    }
    [Test]
    public async Task AutomaticResearchFrequencyCanChangeWithoutRefillingAnExistingGrant()
    {
        var world = await ReadWorld(); await using var db = new SqliteServerDbContext(_options);
        AutomaticSectorResearchTarget[] targets = [new("echo", 3, 6000)];
        await EconomyOperations.RefreshAutomaticSectorResearch(db, world.Cycle, targets, 1000);
        var refreshed = await EconomyOperations.RefreshAutomaticSectorResearch(db, world.Cycle, [new("echo", 3, 42000)], 2000, 45000);
        Assert.That(refreshed.NextRefresh, Is.EqualTo(47000)); Assert.That(refreshed.Created, Is.Zero);
        Assert.That((await EconomyOperations.ReadSectorResearch(db, world.Cycle, _user, await ProfileId(_user))).Orders.Single().Remaining, Is.EqualTo(6000));
    }
#endif
    private async Task<SectorResearchOrder> ResearchOrder(long amount = 6000)
    {
        var world = await ReadWorld(); var profile = await ProfileId(_user);
        var order = new SectorResearchOrder(Guid.NewGuid(), world.Cycle, "echo", "Исследователь", amount, amount, _user, profile, 0, 0, 3, new());
        await using var db = new SqliteServerDbContext(_options); return await EconomyOperations.PublishSectorResearch(db, order);
    }
    [Test]
    public async Task CooperativePlanetPaysOneFiniteFundSplitAcrossCaptainsAndCannotPayAgain()
    {
        var order = await ResearchOrder(6003); var second = await ProfileId(_user, 1);
        var trip = Guid.NewGuid(); var run = Guid.NewGuid();
        await using var db = new SqliteServerDbContext(_options);
        await EconomyOperations.LeaseSectorPlanet(db, order.Cycle, "echo", 0, 3, trip, run, false);
        SectorResearchParticipant[] teams = [new(_user, order.Profile, Guid.Empty), new(_user, second, Guid.Empty), new(_user, second, Guid.Empty)];
        var payments = await EconomyOperations.CompleteSectorPlanetTeams(db, order.Cycle, "echo", 0, trip, run, teams, 20);
        Assert.That(payments, Has.Length.EqualTo(2)); Assert.That(payments.Sum(p => p.Amount), Is.EqualTo(2001));
        Assert.That(payments.Max(p => p.Amount) - payments.Min(p => p.Amount), Is.EqualTo(1));
        Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(800000 - 6003 + payments.Single(p => p.Profile == order.Profile).Amount));
        Assert.That((await EconomyOperations.Read(db, _user, 1)).Bank, Is.EqualTo(500000 + payments.Single(p => p.Profile == second).Amount));
        Assert.That((await EconomyOperations.ReadSectorResearch(db, order.Cycle, _user, order.Profile)).Orders.Single().Remaining, Is.EqualTo(4002));
        Assert.That(await EconomyOperations.CompleteSectorPlanetTeams(db, order.Cycle, "echo", 0, trip, run, teams, 20), Is.Empty);
    }
    private async Task<SectorCrewService> HumanService(int slot = 1, long price = 10000)
    {
        var profile = await ProfileId(_user, slot);
        var service = new SectorCrewService(Guid.NewGuid(), _user, profile, slot, "Капитан", "Шаттл", "Разведка и сопровождение", 3, 2, price, Vessel: Guid.NewGuid());
        await using var db = new SqliteServerDbContext(_options); await EconomyOperations.PublishSectorCrew(db, service); return service;
    }
    [Test]
    public async Task SectorResearchReservesRealMoneyPaysOnlyOnceForACompletedPlanetAndRefundsOnlyRemainingPool()
    {
        var order = await ResearchOrder(); var trip = Guid.NewGuid(); var run = Guid.NewGuid();
        await using (var db = new SqliteServerDbContext(_options))
        {
            await EconomyOperations.PublishSectorResearch(db, order);
            Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(794000));
            await EconomyOperations.LeaseSectorPlanet(db, order.Cycle, "echo", 0, 3, trip, run, false);
            Assert.That(await EconomyOperations.CompleteSectorPlanet(db, order.Cycle, "echo", 0, trip, run, _user, order.Profile, Guid.Empty, 20), Is.EqualTo(2000));
            Assert.That(await EconomyOperations.CompleteSectorPlanet(db, order.Cycle, "echo", 0, trip, run, _user, order.Profile, Guid.Empty, 20), Is.Zero);
        }
        await using (var db = new SqliteServerDbContext(_options))
        {
            Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(796000));
            Assert.That((await EconomyOperations.Read(db, _user, 1)).Bank, Is.EqualTo(500000));
            await EconomyOperations.CancelSectorResearch(db, order.Id, _user, order.Profile); await EconomyOperations.CancelSectorResearch(db, order.Id, _user, order.Profile);
            Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(800000));
        }
    }
    [Test]
    public async Task SectorResearchRequiresItsRealLeaseAndAnAbandonedPlanetDoesNotIncreaseProgressOrPay()
    {
        var order = await ResearchOrder(); var trip = Guid.NewGuid(); var run = Guid.NewGuid();
        await using (var db = new SqliteServerDbContext(_options))
        {
            await EconomyOperations.LeaseSectorPlanet(db, order.Cycle, "echo", 0, 3, trip, run, false);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await EconomyOperations.CompleteSectorPlanet(db, order.Cycle, "echo", 0, Guid.NewGuid(), run, _user, order.Profile, Guid.Empty, 20));
        }
        await using (var db = new SqliteServerDbContext(_options))
        {
            await EconomyOperations.LeaseSectorPlanet(db, order.Cycle, "echo", 0, 3, trip, run, true);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await EconomyOperations.CompleteSectorPlanet(db, order.Cycle, "echo", 0, trip, run, _user, order.Profile, Guid.Empty, 20));
        }
        var world = await ReadWorld(false);
        Assert.That(world.Research["echo"].Completed, Is.Empty);
        await using (var db = new SqliteServerDbContext(_options)) Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(794000));
    }
    [Test]
    public async Task SectorPlanetLeaseBlocksConcurrentTeamsAndCanBeRecoveredAfterAProcessRestart()
    {
        var order = await ResearchOrder(); var trip = Guid.NewGuid(); var run = Guid.NewGuid(); var nextTrip = Guid.NewGuid(); var nextRun = Guid.NewGuid();
        await using (var db = new SqliteServerDbContext(_options))
        {
            await EconomyOperations.LeaseSectorPlanet(db, order.Cycle, "echo", 1, 3, trip, run, false);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await EconomyOperations.LeaseSectorPlanet(db, order.Cycle, "echo", 1, 3, nextTrip, run, false));
        }
        await using (var db = new SqliteServerDbContext(_options)) await EconomyOperations.LeaseSectorPlanet(db, order.Cycle, "echo", 1, 3, nextTrip, nextRun, false);
        await using (var db = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<InvalidOperationException>(async () => await EconomyOperations.CompleteSectorPlanet(db, order.Cycle, "echo", 1, trip, run, _user, order.Profile, Guid.Empty, 20));
        await using (var db = new SqliteServerDbContext(_options))
            Assert.That(await EconomyOperations.CompleteSectorPlanet(db, order.Cycle, "echo", 1, nextTrip, nextRun, _user, order.Profile, Guid.Empty, 20), Is.EqualTo(2000));
    }
    [Test]
    public async Task HumanCrewHireEscrowWaitsForAcceptanceAndCompletionAndCannotPayTwiceOrChangeTheQuotedPrice()
    {
        await ReadWorld(); var offer = await HumanService(); var profile = await ProfileId(_user); var operation = Guid.NewGuid();
        await using (var db = new SqliteServerDbContext(_options))
        {
            await EconomyOperations.HireSectorCrew(db, operation, offer.Id, _user, profile, "Заказчик", "echo", 10000);
            await EconomyOperations.HireSectorCrew(db, operation, offer.Id, _user, profile, "Заказчик", "echo", 10000);
            Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(790000));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await EconomyOperations.SectorHireAction(db, operation, _user, profile, "confirm"));
        }
        await using (var db = new SqliteServerDbContext(_options))
        {
            await EconomyOperations.SectorHireAction(db, operation, _user, offer.Profile, "accept");
            await EconomyOperations.SectorHireAction(db, operation, _user, offer.Profile, "finish");
            await EconomyOperations.SectorHireAction(db, operation, _user, profile, "confirm");
            await EconomyOperations.SectorHireAction(db, operation, _user, profile, "confirm");
            Assert.That((await EconomyOperations.Read(db, _user, 1)).Bank, Is.EqualTo(510000));
            Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(790000));
        }
        var changed = offer with { Price = 15000 };
        await using (var db = new SqliteServerDbContext(_options)) await EconomyOperations.PublishSectorCrew(db, changed);
        await using (var db = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<InvalidOperationException>(async () => await EconomyOperations.HireSectorCrew(db, Guid.NewGuid(), offer.Id, _user, profile, "Заказчик", "echo", 10000));
    }
    [Test]
    public async Task HumanCrewCannotBeHiredTwiceAndFailureReturnsOnlyItsEscrowToTheCorrectCharacter()
    {
        await ReadWorld(); var offer = await HumanService(); var profile = await ProfileId(_user); var hire = Guid.NewGuid();
        await using (var db = new SqliteServerDbContext(_options))
        {
            await EconomyOperations.HireSectorCrew(db, hire, offer.Id, _user, profile, "Заказчик", "echo", offer.Price);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await EconomyOperations.HireSectorCrew(db, Guid.NewGuid(), offer.Id, _user, profile, "Заказчик", "echo", offer.Price));
            var otherViewer = await EconomyOperations.ReadSectorResearch(db, (await ReadWorld()).Cycle, Guid.NewGuid(), 1);
            Assert.That(otherViewer.BusyOffers, Does.Contain(offer.Id));
            Assert.That(otherViewer.Hires, Is.Empty, "Another buyer sees occupancy, never private contracts.");
        }
        await using (var db = new SqliteServerDbContext(_options))
        {
            await EconomyOperations.SectorHireAction(db, hire, _user, offer.Profile, "accept");
            await EconomyOperations.SectorHireAction(db, hire, _user, offer.Profile, "fail");
            await EconomyOperations.SectorHireAction(db, hire, _user, offer.Profile, "fail");
            Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(800000));
            Assert.That((await EconomyOperations.Read(db, _user, 1)).Bank, Is.EqualTo(500000));
        }
    }
    [Test]
    public async Task NormalRoundClosureRefundsUnspentResearchAndUnfulfilledHumanContracts()
    {
        var order = await ResearchOrder(); var offer = await HumanService();
        await using (var db = new SqliteServerDbContext(_options))
        { await EconomyOperations.HireSectorCrew(db, Guid.NewGuid(), offer.Id, _user, order.Profile, "Заказчик", "echo", offer.Price); }
        await using (var db = new SqliteServerDbContext(_options)) await EconomyOperations.CloseVoyageWorld(db, order.Cycle);
        await using (var db = new SqliteServerDbContext(_options))
        {
            Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(800000));
            var board = await EconomyOperations.ReadSectorResearch(db, order.Cycle, _user, order.Profile);
            Assert.That(board.Orders, Is.Empty); Assert.That(board.Hires.Single().Status, Is.EqualTo(5));
        }
    }
    [Test]
    public async Task PirateEncountersPersistAcrossVisitsAndKillsSuppressRoutesIdempotently()
    {
        var world = await ReadWorld(); var route = new[] { "colossus", "helios" }; SectorEncounterInfo encounter;
        await using (var db = new SqliteServerDbContext(_options)) encounter = await EconomyOperations.SectorEncounter(db, world.Cycle, "echo", route, 1, 1000, Guid.Empty, -1);
        Assert.That(encounter.Remaining, Has.Length.EqualTo(3));
        await using (var db = new SqliteServerDbContext(_options))
        { var again = await EconomyOperations.SectorEncounter(db, world.Cycle, "echo", route, 0, 1001, Guid.Empty, -1); Assert.That(again.Id, Is.EqualTo(encounter.Id)); Assert.That(again.Remaining, Has.Length.EqualTo(3)); }
        await using (var db = new SqliteServerDbContext(_options))
        {
            await EconomyOperations.SectorEncounter(db, world.Cycle, "echo", route, 0, 1001, encounter.Id, 0);
            await EconomyOperations.SectorEncounter(db, world.Cycle, "echo", route, 0, 1001, encounter.Id, 0);
            await EconomyOperations.SectorEncounter(db, world.Cycle, "echo", route, 0, 1001, encounter.Id, 1);
            var cleared = await EconomyOperations.SectorEncounter(db, world.Cycle, "echo", route, 0, 1001, encounter.Id, 2); Assert.That(cleared.Remaining, Is.Empty);
        }
        world = await ReadWorld(false, 1001); Assert.That(world.Routes[VoyageRoutes[0]].Value(1001), Is.Zero);
    }
    [TestCase(1, 0, 1)]
    [TestCase(30, 5, 20)]
    [TestCase(3000, 100, 100)]
    public void HumanSectorFlightNeverExceedsTwentySeconds(int minutes, float startup, float travel)
        => Assert.That(SectorResearchDefinition.FlightTime(minutes, startup, travel), Is.InRange(5, 20));
}

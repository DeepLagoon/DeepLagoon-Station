using System;
using System.Collections;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server._Mono.ShipRepair;
using Content.Server._NF.Bank;
using Content.Server._NF.Shipyard.Systems;
using Content.Server.Database;
using Content.Server.Power.Components;
using Content.Server.Preferences.Managers;
using Content.Shared._DeepLagoon.ShuttleParking;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.Access.Components;
using Moq;
using Robust.Shared.Asynchronous;
using Robust.Shared.Containers;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ParkingRepairOrderTests
{
    [TestPrototypes]
    private const string SlowRepairPrototype = @"
- type: entity
  id: ShipyardWorstCaseWallTest
  parent: WallShuttle
  components:
  - type: ShipRepairable
    repairTime: 1000000
    repairCost: 2
";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Complete<T>(ITaskManager tasks, Task<T> task) { tasks.BlockWaitOnTask(task); return task.GetAwaiter().GetResult(); }
    private static int Jobs(ParkingSystem system) => ((IDictionary)typeof(ParkingSystem).GetField("_repairJobs", Private)!.GetValue(system)!).Count;
    private static ParkingRepairView View(ParkingSystem system, EntityUid grid, Guid user) =>
        (ParkingRepairView)typeof(ParkingSystem).GetMethod("BuildRepairView", Private)!.Invoke(system, [grid, user])!;

    [Test]
    public async Task RealConsoleQuotesChargesOnceDelaysCreditsAndRepairsInstantlyForAuthoritativeLC()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var server = pair.Server;
        var e = server.EntMan;
        var map = await pair.CreateTestMap();
        var player = await server.AddDummySession("shipyard_customer");
        var prefs = server.ResolveDependency<IServerPreferencesManager>();
        await PoolManager.WaitUntil(server, () => prefs.TryGetCachedPreferences(player.UserId, out _));
        var tasks = server.ResolveDependency<ITaskManager>();
        var db = server.ResolveDependency<IServerDbManager>();
        var parking = e.System<ParkingSystem>();
        var repairs = e.System<ShipRepairSystem>();
        var now = TimeSpan.FromMinutes(1);
        var timing = new Mock<IGameTiming>(); timing.SetupGet(t => t.RealTime).Returns(() => now);
        EntityUid console = default, body = default, ship = default;
        EconomyAccount before = null!;
        ParkingRepairView quote = null!;

        // BUI dispatch supplies Actor and UiKey. Exercise that same event handler directly in this fixture.
        ParkingRepairAction WithActor(ParkingRepairAction action) { action.Actor = body; action.UiKey = ParkingUiKey.Key; return action; }
        async Task SettleRequest(ParkingRepairAction action)
        {
            await server.WaitPost(() => e.EventBus.RaiseLocalEvent(console, WithActor(action)));
            await PoolManager.WaitUntil(server, () => !((System.Collections.Generic.HashSet<Guid>)typeof(ParkingSystem).GetField("_busy", Private)!.GetValue(parking)!).Contains(player.UserId.UserId));
        }

        await server.WaitAssertion(() =>
        {
            var configuration = server.ResolveDependency<IConfigurationManager>();
            Assert.That(configuration.GetCVar(ShuttleParkingCVars.RepairEnabled), Is.False);
            before = Complete(tasks, db.ReadEconomyAccountAsync(player.UserId.UserId, prefs.GetPreferences(player.UserId).SelectedCharacterIndex));
            body = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            server.PlayerMan.SetAttachedEntity(player, body);
            e.System<BankSystem>().SetConfirmedBankBalance(body, before.Bank);
            console = e.SpawnEntity("ComputerShuttleParking", map.GridCoords);
            e.RemoveComponent<ApcPowerReceiverComponent>(console);
            var maps = e.System<SharedMapSystem>();
            var grid = maps.CreateGridEntity(map.MapId);
            ship = grid.Owner;
            e.EnsureComponent<Content.Server.Shuttles.Components.ShuttleComponent>(ship);
            e.System<SharedTransformSystem>().SetLocalPosition(ship, new Vector2(20, 0));
            for (var x = 0; x < 3; x++)
            for (var y = 0; y < 2; y++)
                maps.SetTile(grid, new Vector2i(x, y), map.Tile.Tile);
            parking.RegisterPurchasedShip(ship, player.UserId.UserId, 1000, "", "Captain");
            repairs.GenerateRepairData(ship);
            maps.SetTile(grid, new Vector2i(1, 0), Tile.Empty);
            maps.SetTile(grid, new Vector2i(2, 0), Tile.Empty);
            e.GetComponent<ParkingShuttleComponent>(ship).Parked = true;
            var card = e.SpawnEntity("PassengerIDCard", map.GridCoords);
            e.GetComponent<IdCardComponent>(card).FullName = e.GetComponent<MetaDataComponent>(body).EntityName;
            e.System<ShipyardSystem>().BindParkingDeed(ship, card, "Shipyard test", "Captain");
            Assert.That(e.System<SharedContainerSystem>().Insert(card, e.GetComponent<ParkingConsoleComponent>(console).Card.ContainerSlot!), Is.True);
            typeof(ParkingSystem).GetField("_timing", Private)!.SetValue(parking, timing.Object);
            Assert.That(typeof(ParkingSystem).GetMethod("GetContext", Private)!.Invoke(parking, [console, body]), Is.Not.Null);
        });
        await SettleRequest(new("quote", ["t:1:0"]));
        await server.WaitAssertion(() =>
        {
            Assert.That(View(parking, ship, player.UserId.UserId).Token, Is.EqualTo(Guid.Empty));
            Assert.That(Jobs(parking), Is.Zero);
            server.ResolveDependency<IConfigurationManager>().SetCVar(ShuttleParkingCVars.RepairEnabled, true);
        });
        await SettleRequest(new("quote", ["t:1:0"]));
        await server.WaitAssertion(() =>
        {
            quote = View(parking, ship, player.UserId.UserId);
            Assert.That(quote.Token, Is.Not.EqualTo(Guid.Empty));
            Assert.That(quote.Selected, Is.EqualTo(new[] { "t:1:0" }));
            Assert.That(quote.Credits, Is.GreaterThanOrEqualTo(quote.Materials));
            Assert.That(quote.Seconds, Is.InRange(1, 3600));
        });
        await SettleRequest(new("confirm", token: quote.Token));
        await server.WaitAssertion(() =>
        {
            Assert.That(Jobs(parking), Is.EqualTo(1));
            Assert.That(repairs.GetShipyardRepairs(ship).Count, Is.EqualTo(2), "Credit repair must wait for the confirmed work time.");
            Assert.That(Complete(tasks, db.ReadEconomyAccountAsync(player.UserId.UserId, before.Slot)).Bank, Is.EqualTo(before.Bank - quote.Credits));
            now += TimeSpan.FromSeconds(quote.Seconds + 1);
        });
        await PoolManager.WaitUntil(server, () => Jobs(parking) == 0);
        await server.WaitAssertion(() => Assert.That(repairs.GetShipyardRepairs(ship).Single().Id, Is.EqualTo("t:2:0")));
        await SettleRequest(new("confirm", token: quote.Token));
        await server.WaitAssertion(() => Assert.That(Complete(tasks, db.ReadEconomyAccountAsync(player.UserId.UserId, before.Slot)).Bank,
            Is.EqualTo(before.Bank - quote.Credits), "A replay must not charge the bank again."));
        await server.WaitAssertion(() => Complete(tasks, db.AwardLagoonCoinsAsync(player.UserId, "test:shipyard-funding", 100, "integration test")));
        var coinBefore = await db.GetLagoonCoinsAsync(player.UserId);
        await SettleRequest(new("quote", ["t:2:0"]));
        await server.WaitAssertion(() => quote = View(parking, ship, player.UserId.UserId));
        await SettleRequest(new("confirm", token: quote.Token, coins: true));
        await PoolManager.WaitUntil(server, () => Jobs(parking) == 0);
        await server.WaitAssertion(() => Assert.That(repairs.GetShipyardRepairs(ship), Is.Empty));
        Assert.That(await db.GetLagoonCoinsAsync(player.UserId), Is.EqualTo(coinBefore - quote.Coins));
        await SettleRequest(new("confirm", token: quote.Token, coins: true));
        Assert.That(await db.GetLagoonCoinsAsync(player.UserId), Is.EqualTo(coinBefore - quote.Coins));

        var slowPart = "";
        await server.WaitAssertion(() =>
        {
            var wall = e.SpawnEntity("ShipyardWorstCaseWallTest", new EntityCoordinates(ship, new Vector2(2.5f, .5f)));
            repairs.GenerateRepairData(ship);
            e.DeleteEntity(wall);
            slowPart = repairs.GetShipyardRepairs(ship).Single().Id;
        });
        await SettleRequest(new("quote", [slowPart]));
        await server.WaitAssertion(() =>
        {
            quote = View(parking, ship, player.UserId.UserId);
            Assert.That(quote.Seconds, Is.EqualTo(3600), "Even a pathological repair time must be capped at one hour.");
            repairs.GenerateRepairData(ship);
        });
        var bankBeforeChangedQuote = (await db.ReadEconomyAccountAsync(player.UserId.UserId, before.Slot)).Bank;
        await SettleRequest(new("confirm", token: quote.Token));
        await server.WaitAssertion(() => Assert.That(Jobs(parking), Is.Zero));
        Assert.That((await db.ReadEconomyAccountAsync(player.UserId.UserId, before.Slot)).Bank, Is.EqualTo(bankBeforeChangedQuote));
        await pair.CleanReturnAsync();
    }
}

using System.Linq;
using Content.Server._DeepLagoon.ShipAssembly;
using Content.Server._NF.Bank;
using Content.Server.Cargo.Components;
using Content.Shared._Crescent.Hardpoints;
using Content.Shared._Mono.CCVar;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Cargo.Events;
using Content.Shared.DeviceLinking;
using Content.Shared.Interaction;
using Content.Shared.Station.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class CargoShipWeaponTests
{
    [TestCase("DLShipWeaponCharon", "WeaponTurretCharonReload", "HardpointBallisticSuperheavy", 2_500_000)]
    [TestCase("DLShipWeaponBofors", "WeaponTurretBofors", "HardpointBallisticHeavy", 1_750_000)]
    [TestCase("DLShipWeaponLongbow", "WeaponTurretLongbow", "HardpointBallisticSuperheavy", 1_550_000)]
    [TestCase("DLShipWeaponTovek", "WeaponTurretTovek", "HardpointMissileMedium", 1_430_000)]
    [TestCase("DLShipWeaponCyrexa", "WeaponTurretCyrexa", "HardpointBallisticHeavy", 1_820_000)]
    public async Task OrderDeliversOnePackagedWeaponForInstallationOnASeparateHardpoint(
        string productId, string weaponId, string hardpointId, int price)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var entities = server.EntMan;
        var deliveryCoords = new EntityCoordinates(map.Grid, 1.5f, 0.5f);
        var installCoords = new EntityCoordinates(map.Grid, 2.5f, 0.5f);
        StationCargoOrderDatabaseComponent orders = default;
        EntityUid actor = default;
        EntityUid kit = default;
        string orderedKitId = default;

        await server.WaitAssertion(() =>
        {
            var maps = server.System<SharedMapSystem>();
            for (var x = 0; x < 3; x++)
                maps.SetTile(map.Grid, new Vector2i(x, 0), map.Tile.Tile);

            var station = entities.SpawnEntity(null, map.GridCoords);
            orders = entities.AddComponent<StationCargoOrderDatabaseComponent>(station);
            entities.EnsureComponent<StationMemberComponent>(map.Grid).Station = station;
            var console = entities.SpawnEntity("ComputerCargoOrders", map.GridCoords);
            var telepad = entities.SpawnEntity("CargoTelepad", deliveryCoords);
            entities.System<SharedDeviceLinkSystem>().SaveLinks(null, console, telepad, [("OrderSender", "OrderReceiver")]);

            actor = entities.SpawnEntity("MobHumanDummy", map.GridCoords);
            var account = entities.EnsureComponent<BankAccountComponent>(actor);
            var session = server.PlayerMan.Sessions.Single();
            server.PlayerMan.SetAttachedEntity(session, actor);
            server.CfgMan.SetCVar(MonoCVars.DepositEnabled, true);
            Assert.That(entities.System<BankSystem>().TryBankDeposit(actor, price), Is.True);
            var fundedBalance = account.Balance;

            entities.EventBus.RaiseLocalEvent(console, new CargoConsoleAddOrderMessage("Test", "", productId, 1) { Actor = actor });
            var order = orders.Orders.Single();
            Assert.That(order.Price, Is.EqualTo(price));
            orderedKitId = order.ProductId;

            entities.EventBus.RaiseLocalEvent(console, new CargoConsoleApproveOrderMessage(order.OrderId) { Actor = actor });
            Assert.That(order.Approved, Is.True);
            Assert.That(account.Balance, Is.EqualTo(fundedBalance - price));
            server.PlayerMan.SetAttachedEntity(session, null);
        });

        await server.WaitRunTicks(11 * server.Timing.TickRate);
        await server.WaitAssertion(() =>
        {
            Assert.That(orders.Orders, Is.Empty);
            var delivered = entities.EntityQuery<ShipAssemblyKitComponent, TransformComponent>()
                .Where(entry => entry.Item2.GridUid == map.Grid.Owner).Single();
            kit = delivered.Item1.Owner;
            Assert.That(delivered.Item2.Coordinates, Is.EqualTo(deliveryCoords));
            Assert.That(entities.GetComponent<MetaDataComponent>(kit).EntityPrototype!.ID, Is.EqualTo(orderedKitId));
            Assert.That(entities.EntityQuery<HardpointComponent, TransformComponent>()
                .Any(entry => entry.Item2.GridUid == map.Grid.Owner), Is.False);
            Assert.That(entities.EntityQuery<HardpointAnchorableOnlyComponent, TransformComponent>()
                .Any(entry => entry.Item2.GridUid == map.Grid.Owner), Is.False);

            var hardpoint = entities.SpawnEntity(hardpointId, installCoords);
            entities.EventBus.RaiseLocalEvent(kit, new AfterInteractEvent(actor, kit, hardpoint, installCoords, canReach: true));
            var installed = entities.GetComponent<HardpointComponent>(hardpoint).anchoring;
            Assert.That(installed, Is.Not.Null);
            Assert.That(entities.GetComponent<MetaDataComponent>(installed!.Value).EntityPrototype!.ID, Is.EqualTo(weaponId));
        });

        await server.WaitRunTicks(2);
        await server.WaitAssertion(() => Assert.That(entities.EntityExists(kit), Is.False));
        await pair.CleanReturnAsync();
    }
}

using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Client._DeepLagoon.ShuttleParking;
using Content.Client._DeepLagoon.WebUI;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server._NF.Shipyard.Systems;
using Content.Server.Power.Components;
using Content.Server.Shuttles.Components;
using Content.Shared._DeepLagoon.ShuttleParking;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ParkingIdentityTests
{
    private const string CharacterName = "Проклятая Последовательница Цефе";
    private const string TruncatedName = "Проклятая Последовательница Це";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task IdCardPreservesFullLengthCharacterName()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var card = entities.SpawnEntity("PassengerIDCard", map.GridCoords);
            Assert.That(entities.System<SharedIdCardSystem>().TryChangeFullName(card, CharacterName), Is.True);
            Assert.That(entities.GetComponent<IdCardComponent>(card).FullName, Is.EqualTo(CharacterName),
                "A valid 32-character profile name must not be truncated when written to an ID");
            Assert.That(entities.GetComponent<MetaDataComponent>(card).EntityName, Does.Contain(CharacterName));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ParkingAcceptsMismatchedNamesButStillChecksAccountOwnershipAndAccess()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        EntityUid console = default, actor = default, card = default;
        NetEntity netConsole = default;
        var getContext = typeof(ParkingSystem).GetMethod("GetContext", PrivateInstance)!;
        var ownedShip = typeof(ParkingSystem).GetMethod("OwnedShip", PrivateInstance)!;

        await pair.Server.WaitAssertion(() =>
        {
            var session = pair.Server.PlayerMan.Sessions.Single();
            var maps = entities.System<SharedMapSystem>();
            maps.SetTile(map.Grid, new Vector2i(1, 0), map.Tile.Tile);
            actor = entities.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(1.5f, 0.5f)));
            entities.System<MetaDataSystem>().SetEntityName(actor, CharacterName);
            pair.Server.PlayerMan.SetAttachedEntity(session, actor);
            console = entities.SpawnEntity("ComputerShuttleParking", map.GridCoords.Offset(new Vector2(0.5f)));
            netConsole = entities.GetNetEntity(console);
            var power = entities.GetComponent<ApcPowerReceiverComponent>(console);
            power.Powered = true;
            card = entities.SpawnEntity("PassengerIDCard", map.GridCoords);
            var slot = entities.GetComponent<ParkingConsoleComponent>(console).Card.ContainerSlot!;
            var containers = entities.System<SharedContainerSystem>();
            Assert.That(containers.Insert(card, slot), Is.True);

            var ship = maps.CreateGridEntity(map.MapId);
            maps.SetTile(ship, Vector2i.Zero, map.Tile.Tile);
            entities.EnsureComponent<ShuttleComponent>(ship.Owner);
            var parking = entities.System<ParkingSystem>();
            parking.RegisterPurchasedShip(ship.Owner, session.UserId.UserId, 1_000, "", CharacterName);
            entities.System<ShipyardSystem>().BindParkingDeed(ship.Owner, card, "Тестовый шаттл", CharacterName);
            var ownership = entities.GetComponent<ParkingShuttleComponent>(ship.Owner);

            foreach (var name in new[] { TruncatedName, "Другой персонаж", null })
            {
                entities.GetComponent<IdCardComponent>(card).FullName = name;
                var context = getContext.Invoke(parking, [console, actor]);
                Assert.That(context, Is.Not.Null, "ID names do not authenticate the console user");
                Assert.That(ownedShip.Invoke(parking, [context]), Is.EqualTo(ship.Owner));
                ownership.OwnerAccount = Guid.NewGuid();
                Assert.That(ownedShip.Invoke(parking, [context]), Is.Null,
                    "An inserted deed must not grant access to another account's ship");
                ownership.OwnerAccount = session.UserId.UserId;
            }

            power.Powered = false;
            Assert.That(getContext.Invoke(parking, [console, actor]), Is.Null, "Power is still required");
            entities.RemoveComponent<ApcPowerReceiverComponent>(console);
            entities.GetComponent<IdCardComponent>(card).FullName = TruncatedName;
            entities.System<Robust.Server.GameObjects.UserInterfaceSystem>().OpenUi(console, ParkingUiKey.Key, actor);
        });
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
        {
            var client = pair.Client.EntMan;
            var ui = client.System<Robust.Client.GameObjects.UserInterfaceSystem>();
            Assert.That(ui.TryGetOpenUi(client.GetEntity(netConsole), ParkingUiKey.Key, out var bui), Is.True);
            var window = (ParkingWindow) typeof(ParkingBoundUserInterface).GetField("_window", PrivateInstance)!.GetValue(bui)!;
            var json = (string) typeof(TguiPanel).GetField("_data", PrivateInstance)!.GetValue(window.Panel)!;
            var data = NativeUiData.Parse(json);
            Assert.That(data.Bool("id"), Is.True, json);
            Assert.That(data.Bool("hasShip"), Is.True, json);
            bui!.Close();
        });
        await pair.RunTicksSync(5);
        await pair.Server.WaitAssertion(() =>
        {
            var parking = entities.System<ParkingSystem>();
            entities.RemoveComponent<IdCardComponent>(card);
            Assert.That(getContext.Invoke(parking, [console, actor]), Is.Null, "The inserted item must still be an ID");
            entities.System<SharedContainerSystem>().Remove(card,
                entities.GetComponent<ParkingConsoleComponent>(console).Card.ContainerSlot!);
            Assert.That(getContext.Invoke(parking, [console, actor]), Is.Null, "An inserted ID is still required");
        });
        await pair.CleanReturnAsync();
    }
}

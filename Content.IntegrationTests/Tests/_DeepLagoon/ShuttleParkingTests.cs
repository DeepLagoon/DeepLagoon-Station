using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Content.Client._DeepLagoon.ShuttleParking;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server._Mono.FireControl;
using Content.Server._NF.Shipyard.Systems;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Construction;
using Content.Server.Construction.Components;
using Content.Server.Database;
using Content.Server.Decals;
using Content.Server.DeviceLinking.Systems;
using Content.Server.Lathe;
using Content.Server.Power.Components;
using Content.Server.Power.Generator;
using Content.Server.Research.Systems;
using Content.Shared._Crescent.Hardpoints;
using Content.Shared._Crescent.ShipShields;
using Content.Shared._DeepLagoon.ShuttleParking;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Atmos;
using Content.Shared._Mono.Traits.Physical;
using Content.Shared.Clothing.Components;
using Content.Shared.Lathe;
using Content.Shared.Paper;
using Content.Shared.Preferences;
using Content.Shared.Power.Generator;
using Content.Shared.Research.Components;
using Content.Shared.Research.Prototypes;
using Content.Shared.Shuttles.Components;
using Content.Shared.SmartFridge;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Systems;
using Content.Shared.Station.Components;
using Microsoft.Data.Sqlite;
using Moq;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Map.Events;
using Robust.Shared.Maths;
using Robust.Shared.Asynchronous;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using Content.Client._DeepLagoon.WebUI;
using Robust.Client.UserInterface;
using Button = Robust.Client.UserInterface.Controls.Button;
using System.Reflection;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ShuttleParkingTests
{
    private sealed class ParkingMigrationTestSystem : EntitySystem
    {
        public string? DeletedPrototype;
        public int LoadsBeforeDeletion;

        public override void Initialize()
        {
            SubscribeLocalEvent<BeforeEntityReadEvent>(OnBeforeRead);
        }

        private void OnBeforeRead(BeforeEntityReadEvent ev)
        {
            if (DeletedPrototype == null)
                return;
            if (LoadsBeforeDeletion > 0)
                LoadsBeforeDeletion--;
            else
                ev.DeletedPrototypes.Add(DeletedPrototype);
        }
    }

    [Test]
    public async Task NativeParkingCountdownTracksDeadlineUntilServerEnablesReturn()
    {
        await using var pair = await PoolManager.GetServerClient();
        await pair.Client.WaitAssertion(() =>
        {
            using var panel = new NativeInterfacePanel((_, _) => { }, () => { });
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(NativeInterfacePanel).GetField("_parkingTab", flags)!.SetValue(panel, 1);
            var readyAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 65_000;
            var slot = Guid.NewGuid().ToString();
            string State(int wait) => new TguiData().Bool("id", true)
                .Array("docks", [new TguiData().String("id", "101").String("name", "Шлюз")])
                .Array("places", [new TguiData().String("id", slot).String("name", "Orion-P").Number("price", 0)
                    .Number("returnWaitSeconds", wait).Number("returnReadyAtMs", wait > 0 ? readyAtMs : 0)]).ToString();

            static IEnumerable<Control> Descendants(Control control) => control.Children.SelectMany(child => Descendants(child).Prepend(child));
            Button ReturnButton() => Descendants(panel).OfType<Button>().Single(button => button.Text == "Вернуть бесплатно…");

            panel.UpdateState("ShuttleParking", State(65));
            var countdown = Descendants(panel).OfType<Robust.Client.UserInterface.Controls.RichTextLabel>()
                .Single(label => label.Text?.StartsWith("Бесплатный возврат через ", StringComparison.Ordinal) == true);
            var update = countdown.GetType().GetMethod("UpdateCountdown", flags)!;
            update.Invoke(countdown, [readyAtMs - 65_000]);
            Assert.That(countdown.Text, Is.EqualTo("Бесплатный возврат через 1:05"));
            update.Invoke(countdown, [readyAtMs - 4_001]);
            Assert.That(countdown.Text, Is.EqualTo("Бесплатный возврат через 0:05"));
            update.Invoke(countdown, [readyAtMs]);
            Assert.That(countdown.Text, Is.EqualTo("Бесплатный возврат через 0:00"));
            Assert.That(ReturnButton().Disabled, Is.True);

            panel.UpdateState("ShuttleParking", State(0));
            Assert.That(ReturnButton().Disabled, Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ParkingCountdownResumesWhenConsoleReopens()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        var server = pair.Server.EntMan;
        var client = pair.Client.EntMan;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        EntityUid console = default, actor = default;
        NetEntity netConsole = default;
        var now = TimeSpan.FromMinutes(1);
        var slot = Guid.NewGuid();
        var clock = new Mock<IGameTiming>();
        clock.SetupGet(timing => timing.RealTime).Returns(() => now);

        await pair.Server.WaitAssertion(() =>
        {
            var session = pair.Server.PlayerMan.Sessions.Single();
            server.System<SharedMapSystem>().SetTile(map.Grid, new Vector2i(1, 0), map.Tile.Tile);
            actor = server.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(1.5f, 0.5f)));
            pair.Server.PlayerMan.SetAttachedEntity(session, actor);
            console = server.SpawnEntity("ComputerShuttleParking", map.GridCoords.Offset(new Vector2(0.5f)));
            netConsole = server.GetNetEntity(console);
            server.RemoveComponent<ApcPowerReceiverComponent>(console);
            var card = server.SpawnEntity("PassengerIDCard", map.GridCoords);
            server.GetComponent<Content.Shared.Access.Components.IdCardComponent>(card).FullName =
                server.GetComponent<MetaDataComponent>(actor).EntityName;
            Assert.That(server.System<SharedContainerSystem>().Insert(card, server.GetComponent<ParkingConsoleComponent>(console).Card.ContainerSlot!), Is.True);

            var parking = server.System<ParkingSystem>();
            typeof(ParkingSystem).GetField("_timing", flags)!.SetValue(parking, clock.Object);
            var shift = (Guid) typeof(ParkingSystem).GetField("_shift", flags)!.GetValue(parking)!;
            var store = (ParkingStore) typeof(ParkingSystem).GetField("_store", flags)!.GetValue(parking)!;
            var owner = session.UserId.UserId;
            store.GrantPurchase(owner, store.BeginPurchase(owner, slot, 0));
            store.Save(owner, slot, new StoredShuttle(Guid.NewGuid(), "Orion-P", "Капитан", 0, 1_000, "", ParkingStore.Hash(""), SavedShift: shift));
            store.FinishSave(owner, slot, now + ParkingRules.FreeReturnDelay);
        });
        await pair.RunTicksSync(5);

        async Task CheckOpening(int elapsed, int expectedWaitSeconds)
        {
            var openedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await pair.Server.WaitAssertion(() =>
            {
                now = TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(elapsed);
                var parking = server.System<ParkingSystem>();
                Assert.That(typeof(ParkingSystem).GetMethod("GetContext", flags)!.Invoke(parking, [console, actor]), Is.Not.Null);
                server.System<Robust.Server.GameObjects.UserInterfaceSystem>().OpenUi(console, ParkingUiKey.Key, actor);
            });
            await pair.RunTicksSync(15);
            await pair.Client.WaitAssertion(() =>
            {
                var ui = client.System<Robust.Client.GameObjects.UserInterfaceSystem>();
                var entity = client.GetEntity(netConsole);
                Assert.That(ui.TryGetOpenUi(entity, ParkingUiKey.Key, out var bui), Is.True);
                var window = (ParkingWindow) typeof(ParkingBoundUserInterface).GetField("_window", flags)!.GetValue(bui)!;
                var json = (string) typeof(TguiPanel).GetField("_data", flags)!.GetValue(window.Panel)!;
                var data = NativeUiData.Parse(json);
                Assert.That(data.Bool("id"), Is.True, json);
                var place = data.Array("places").Single(place => place.String("id") == slot.ToString());
                Assert.That(place.Int("returnWaitSeconds"), Is.EqualTo(expectedWaitSeconds));
                Assert.That(place.Int("price"), Is.Zero);
                var readyAtMs = long.Parse(place.String("returnReadyAtMs"));
                var remainingMs = expectedWaitSeconds * 1000L;
                Assert.That(readyAtMs, expectedWaitSeconds == 0 ? Is.Zero
                    : Is.InRange(openedAtMs + remainingMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + remainingMs));
                bui!.Close();
            });
            await pair.RunTicksSync(10);
            await pair.Client.WaitAssertion(() =>
                Assert.That(client.System<Robust.Client.GameObjects.UserInterfaceSystem>()
                    .TryGetOpenUi(client.GetEntity(netConsole), ParkingUiKey.Key, out _), Is.False));
        }

        await CheckOpening(0, 300);
        await CheckOpening(90, 210);
        await CheckOpening(300, 0);
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ParkingSnapshotSurvivesUnserializableContentsAndDeletedPrototypes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out var map);
            var grid = maps.CreateGridEntity(map);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTile(grid, Vector2i.Zero, floor);
            var position = new EntityCoordinates(grid.Owner, new Vector2(.5f));
            var bag = entities.SpawnEntity("ClothingBackpack", position);
            var invalid = entities.SpawnEntity(null, position);
            entities.AddComponent<GridEditorBrokenExportComponent>(invalid);
            var child = entities.SpawnEntity(null, new EntityCoordinates(invalid, Vector2.Zero));
            var healthy = entities.SpawnEntity("Crowbar", position);
            var migrated = entities.SpawnEntity("ChairWood", position);
            var containers = entities.System<SharedContainerSystem>();
            var inventory = containers.EnsureContainer<Container>(bag, "test-inventory");
            Assert.That(containers.Insert(invalid, inventory), Is.True);
            Assert.That(containers.Insert(healthy, inventory), Is.True);

            var parking = entities.System<ParkingSystem>();
            parking.RegisterPurchasedShip(grid.Owner, Guid.NewGuid(), 1000, "", "Captain");
            var migration = entities.System<ParkingMigrationTestSystem>();
            var failureLevel = pair.ServerLogHandler.FailureLevel;

            string snapshot;
            try
            {
                migration.DeletedPrototype = "ChairWood";
                pair.ServerLogHandler.FailureLevel = LogLevel.Fatal;
                object?[] args = [grid.Owner, null, null];
                var method = typeof(ParkingSystem).GetMethod("FilteredSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
                snapshot = (string) method.Invoke(parking, args)!;
                Assert.That(args[1], Is.EquivalentTo(new[] { invalid, child, migrated }));
                Assert.That((HashSet<EntityUid>) args[2]!, Does.Contain(healthy));
            }
            finally
            {
                pair.ServerLogHandler.FailureLevel = failureLevel;
                migration.DeletedPrototype = null;
            }

            Assert.That(entities.EntityExists(invalid) && entities.EntityExists(child) && entities.EntityExists(migrated), Is.True);
            var restored = parking.LoadSnapshot(snapshot);
            var restoredBag = entities.EntityQuery<MetaDataComponent, TransformComponent>(true)
                .Single(entry => entry.Item2.GridUid == restored && entry.Item1.EntityPrototype?.ID == "ClothingBackpack").Item1.Owner;
            var restoredItem = containers.GetContainer(restoredBag, "test-inventory").ContainedEntities.Single();
            Assert.That(entities.GetComponent<MetaDataComponent>(restoredItem).EntityPrototype?.ID, Is.EqualTo("Crowbar"));
            Assert.That(entities.EntityQuery<MetaDataComponent, TransformComponent>(true)
                .Any(entry => entry.Item2.GridUid == restored && entry.Item1.EntityPrototype?.ID == "ChairWood"), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OrionPBlueprintRoundTripsThroughLongTermParking()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            entities.System<SharedMapSystem>().CreateMap(out var map);
            var path = new Robust.Shared.Utility.ResPath("/SharedMaps/_Mono/Shuttles/Imported/Expedition/orion-p.yml");
            Assert.That(entities.System<MapLoaderSystem>().TryLoadGrid(map, path, out var loaded), Is.True);
            var grid = loaded!.Value.Owner;
            var parking = entities.System<ParkingSystem>();
            parking.RegisterPurchasedShip(grid, Guid.NewGuid(), 237000, "Orion-P", "Captain");
            Assert.That(parking.FilteredSnapshot(grid), Is.Not.Empty);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SmartArmoryRestoresInventoryIndexAfterParking()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out var map);
            var grid = maps.CreateGridEntity(map);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTile(grid, Vector2i.Zero, floor);
            var position = new EntityCoordinates(grid.Owner, new Vector2(.5f));
            var armory = entities.SpawnEntity("SmartArmoryStorage", position);
            var fridge = entities.GetComponent<SmartFridgeComponent>(armory);
            var containers = entities.System<SharedContainerSystem>();
            var inventory = containers.GetContainer(armory, fridge.Container);
            var originalGun = entities.SpawnEntity("WeaponPistolMk58", position);
            var removedSword = entities.SpawnEntity("Claymore", position);
            var gunEntry = new SmartFridgeEntry(entities.GetComponent<MetaDataComponent>(originalGun).EntityName);
            var emptyEntry = new SmartFridgeEntry(entities.GetComponent<MetaDataComponent>(removedSword).EntityName);
            Assert.That(containers.Insert(originalGun, inventory), Is.True);
            Assert.That(containers.Insert(removedSword, inventory), Is.True);
            Assert.That(containers.TryRemoveFromContainer(removedSword), Is.True);
            entities.DeleteEntity(removedSword);

            var parking = entities.System<ParkingSystem>();
            parking.RegisterPurchasedShip(grid.Owner, Guid.NewGuid(), 1000, "", "Captain");
            var extraGun = entities.SpawnEntity("WeaponPistolMk58", position);
            Assert.That(containers.Insert(extraGun, inventory), Is.True);
            Assert.That(fridge.ContainedEntries[gunEntry], Has.Count.EqualTo(2));

            var snapshot = parking.FilteredSnapshot(grid.Owner);
            Assert.That(snapshot, Does.Not.Contain("containedEntries:"));
            var restoredGrid = parking.LoadSnapshot(snapshot);
            var restoredFridge = entities.EntityQuery<SmartFridgeComponent, TransformComponent>(true)
                .Single(entry => entry.Item2.GridUid == restoredGrid).Item1;
            var restoredGun = containers.GetContainer(restoredFridge.Owner, restoredFridge.Container).ContainedEntities.Single();
            Assert.That(restoredFridge.Entries, Does.Contain(emptyEntry));
            Assert.That(restoredFridge.ContainedEntries[emptyEntry], Is.Empty);
            Assert.That(restoredFridge.ContainedEntries[gunEntry], Is.EquivalentTo(new[] { entities.GetNetEntity(restoredGun) }));
            Assert.That(restoredGun, Is.Not.EqualTo(originalGun));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LongTermParkingRestoresInstalledEquipmentAndItsConnections()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var entities = pair.Server.EntMan;
        EntityUid grid = default;
        EntityUid generator = default;
        EntityUid emitter = default;
        EntityUid source = default;
        EntityUid weapon = default;
        EntityUid mount = default;
        EntityUid research = default;
        EntityUid researchConsole = default;
        EntityUid gunnery = default;
        EntityUid pistol = default;
        EntityUid outside = default;
        EntityUid otherOutside = default;
        MapId destination = default;
        const string Weapon = "WeaponTurretL85Autocannon";
        const string Mount = "HardpointBallisticLight";
        const string Generator = "PortableGeneratorPacmanShuttle";
        const string Emitter = "ShieldGeneratorSmall";
        const string Switch = "SignalSwitch";
        const string Research = "ResearchAndDevelopmentServer";
        const string ResearchConsole = "ComputerResearchAndDevelopment";
        const string Gunnery = "GunneryServerLow";

        await pair.Server.WaitAssertion(() =>
        {
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out destination);
            var map = maps.CreateMap(out var mapId);
            var ship = maps.CreateGridEntity(mapId);
            grid = ship.Owner;
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var x = 0; x < 8; x++)
                maps.SetTile(ship, new Vector2i(x, 0), floor);

            EntityUid Spawn(string id, int x) => entities.SpawnEntity(id, new EntityCoordinates(grid, new Vector2(x + .5f, .5f)));
            entities.System<ParkingSystem>().RegisterPurchasedShip(grid, Guid.NewGuid(), 1000, "", "Captain");
            mount = Spawn(Mount, 0);
            weapon = Spawn(Weapon, 0);
            var transforms = entities.System<SharedTransformSystem>();
            if (!entities.GetComponent<TransformComponent>(weapon).Anchored)
                transforms.AnchorEntity(weapon, entities.GetComponent<TransformComponent>(weapon));

            generator = Spawn(Generator, 1);
            source = Spawn(Switch, 2);
            emitter = Spawn(Emitter, 3);
            research = Spawn(Research, 4);
            researchConsole = Spawn(ResearchConsole, 5);
            gunnery = Spawn(Gunnery, 6);
            pistol = Spawn("WeaponPistolMk58", 7);
            outside = entities.SpawnEntity(Generator, new EntityCoordinates(map, new Vector2(10, 0)));
            otherOutside = entities.SpawnEntity(Generator, new EntityCoordinates(map, new Vector2(11, 0)));

            // Power the gunnery server and emitter while testing the fuel generator separately.
            foreach (var machine in new[] { gunnery, emitter })
                entities.GetComponent<ApcPowerReceiverComponent>(machine).NeedsPower = false;

            var construction = entities.System<ConstructionSystem>();
            var machineParts = entities.GetComponent<MachineComponent>(generator).PartContainer;
            var containers = entities.System<SharedContainerSystem>();
            foreach (var part in construction.GetAllParts(generator).Where(part => part.Part.PartType == "Capacitor").ToArray())
                entities.DeleteEntity(part.Part.Owner);
            var upgrade = entities.SpawnEntity("SuperCapacitorStockPart", new EntityCoordinates(grid, Vector2.One));
            Assert.That(containers.Insert(upgrade, machineParts), Is.True);
            construction.RefreshParts(generator, entities.GetComponent<MachineComponent>(generator));

            entities.System<ResearchSystem>().RegisterClient(researchConsole, research);
            var links = entities.System<DeviceLinkSystem>();
            foreach (var sink in new[] { generator, outside, otherOutside })
                links.SaveLinks(null, source, sink, [("Off", "Off")]);
        });

        await pair.RunSeconds(2.5f);
        await pair.Server.WaitAssertion(() =>
        {
            var parking = entities.System<ParkingSystem>();
            var links = entities.System<DeviceLinkSystem>();
            Assert.That(entities.GetComponent<HardpointComponent>(mount).anchoring, Is.EqualTo(weapon));
            Assert.That(entities.GetComponent<FireControlServerComponent>(gunnery).Controlled, Does.Contain(weapon));
            Assert.That(entities.GetComponent<ShipShieldEmitterComponent>(emitter).Shield, Is.Not.Null);
            Assert.That(links.GetLinks(source, generator), Has.Count.EqualTo(1));
            var excluded = parking.ExcludedEntities(grid);
            Assert.That(excluded, Does.Contain(pistol));
            Assert.That(excluded, Does.Not.Contain(weapon));
            Assert.That(excluded, Does.Not.Contain(generator));

            var supply = entities.GetComponent<PowerSupplierComponent>(generator).MaxSupply;
            Assert.That(supply, Is.GreaterThan(15000));
            var transforms = entities.System<SharedTransformSystem>();
            var maps = entities.System<SharedMapSystem>();
            var parkedMap = maps.CreateMap(out var parkedId);
            maps.SetPaused(parkedId, true);
            transforms.SetCoordinates(grid, new EntityCoordinates(parkedMap, Vector2.Zero));
            var snapshot = parking.FilteredSnapshot(grid);
            Assert.That(links.GetLinks(source, outside), Has.Count.EqualTo(1));
            Assert.That(links.GetLinks(source, otherOutside), Has.Count.EqualTo(1));
            EntityUid Find(EntityUid restored, string id) => entities.EntityQuery<MetaDataComponent, TransformComponent>(true)
                .Single(entry => entry.Item1.EntityPrototype?.ID == id && entry.Item2.GridUid == restored).Item1.Owner;

            // Older snapshots omitted the selected research server and could contain stale shield state.
            var legacy = new YamlStream();
            legacy.Load(new StringReader(snapshot));
            var data = (YamlMappingNode) legacy.Documents.Single().RootNode;
            var groups = (YamlSequenceNode) data["entities"];
            var gridId = ((YamlSequenceNode) data["grids"]).Children.Single();
            var gridNode = groups.Children.Cast<YamlMappingNode>()
                .SelectMany(group => ((YamlSequenceNode) group["entities"]).Children.Cast<YamlMappingNode>())
                .Single(node => node["uid"].Equals(gridId));
            var gridComponents = (YamlSequenceNode) gridNode["components"];
            var savedShip = gridComponents.Children.Cast<YamlMappingNode>()
                .Single(node => node["type"].ToString() == "ParkingShuttle");
            Assert.That(savedShip.Children.Remove(new YamlScalarNode("researchConnections")), Is.True);
            gridComponents.Add(new YamlMappingNode { { "type", "ShipShielded" } });
            using var writer = new StringWriter();
            legacy.Save(new YamlMappingFix(new Emitter(writer)), assignAnchors: false);
            var legacyGrid = parking.LoadSnapshot(writer.ToString());
            Assert.That(entities.HasComponent<ShipShieldedComponent>(legacyGrid), Is.False);
            Assert.That(entities.GetComponent<ResearchClientComponent>(Find(legacyGrid, ResearchConsole)).Server,
                Is.EqualTo(Find(legacyGrid, Research)));
            entities.DeleteEntity(legacyGrid);

            var restored = parking.LoadSnapshot(snapshot);
            Assert.That(entities.HasComponent<ShipShieldedComponent>(restored), Is.False);
            Assert.That(entities.EntityQuery<ShipShieldComponent, TransformComponent>(true)
                .Any(entry => entry.Item2.GridUid == restored), Is.False);
            mount = Find(restored, Mount);
            weapon = Find(restored, Weapon);
            generator = Find(restored, Generator);
            source = Find(restored, Switch);
            research = Find(restored, Research);
            researchConsole = Find(restored, ResearchConsole);
            gunnery = Find(restored, Gunnery);
            emitter = Find(restored, Emitter);

            Assert.That(entities.GetComponent<HardpointComponent>(mount).anchoring, Is.EqualTo(weapon));
            Assert.That(entities.GetComponent<ResearchClientComponent>(researchConsole).Server, Is.EqualTo(research));
            Assert.That(entities.GetComponent<MachineComponent>(generator).BoardContainer.ContainedEntities, Has.Count.EqualTo(1));
            Assert.That(entities.GetComponent<PowerSupplierComponent>(generator).MaxSupply, Is.EqualTo(supply).Within(0.01f));
            Assert.That(links.GetLinks(source, generator), Has.Count.EqualTo(1));
            Assert.That(links.GetLinks(source, outside), Is.Empty);
            Assert.That(links.GetLinks(source, otherOutside), Is.Empty);
            transforms.SetCoordinates(restored, new EntityCoordinates(maps.GetMap(destination), Vector2.Zero));
        });

        await pair.RunSeconds(2.5f);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(entities.GetComponent<FireControlServerComponent>(gunnery).Controlled, Does.Contain(weapon));
            Assert.That(entities.GetComponent<ShipShieldEmitterComponent>(emitter).Shield, Is.Not.Null);
            Assert.That(entities.GetComponent<FuelGeneratorComponent>(generator).On, Is.True);
            entities.System<DeviceLinkSystem>().InvokePort(source, "Off");
        });
        await pair.RunTicksSync(3);
        await pair.Server.WaitAssertion(() =>
            Assert.That(entities.GetComponent<FuelGeneratorComponent>(generator).On, Is.False));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RecallUsesShuttleHullAndShowsRealObstructions()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var transforms = entities.System<SharedTransformSystem>();
            var map = maps.CreateMap(out var mapId);
            maps.CreateMap(out var parkingMap);
            var stationGrid = maps.CreateGridEntity(mapId);
            var ship = maps.CreateGridEntity(parkingMap);
            var blocker = maps.CreateGridEntity(mapId);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var x = 0; x < 3; x++)
                for (var y = 0; y < 3; y++)
                    maps.SetTile(stationGrid, new Vector2i(x, y), floor);
            for (var y = 0; y < 3; y++)
                maps.SetTile(ship, new Vector2i(0, y), floor);
            for (var x = 1; x < 4; x++)
                maps.SetTile(ship, new Vector2i(x, 0), floor);
            maps.SetTile(blocker, Vector2i.Zero, floor);
            transforms.SetCoordinates(blocker.Owner, new EntityCoordinates(map, new Vector2(5, 2)));

            var station = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            entities.EnsureComponent<StationDataComponent>(station);
            entities.System<StationSystem>().AddGridToStation(station, stationGrid.Owner);
            var console = entities.SpawnEntity("ComputerShuttleParking", new EntityCoordinates(stationGrid.Owner, new Vector2(.5f)));
            var stationPort = entities.SpawnEntity("AirlockShuttle", new EntityCoordinates(stationGrid.Owner, new Vector2(2.5f, 1.5f)));
            var shipPort = entities.SpawnEntity("AirlockShuttle", new EntityCoordinates(ship.Owner, new Vector2(.5f, 1.5f)));
            transforms.SetLocalRotation(stationPort, Angle.FromDegrees(90));
            transforms.SetLocalRotation(shipPort, Angle.FromDegrees(270));

            var docking = entities.System<DockingSystem>();
            var shipDock = entities.GetComponent<DockingComponent>(shipPort);
            var stationDock = entities.GetComponent<DockingComponent>(stationPort);
            Assert.That(docking.GetDockingConfig(ship.Owner, stationGrid.Owner, shipPort, shipDock, stationPort, stationDock), Is.Null,
                "The obstruction lies inside the old bounding rectangle");
            var parking = entities.System<ParkingSystem>();
            var buildMap = typeof(ParkingSystem).GetMethod("BuildRecallMap", BindingFlags.NonPublic | BindingFlags.Instance)!;
            ParkingRecallMap GetRecallMap() => (ParkingRecallMap) buildMap.Invoke(parking, [console, ship.Owner])!;

            var clear = GetRecallMap();
            Assert.That(clear.Ports.Single().Status, Is.EqualTo(ParkingPortStatus.Fits));

            transforms.SetCoordinates(blocker.Owner, new EntityCoordinates(map, new Vector2(5, 0)));
            var obstructed = GetRecallMap();
            Assert.That(obstructed.Ports.Single().Status, Is.EqualTo(ParkingPortStatus.Blocked));
            Assert.That(obstructed.StationHull.Count, Is.EqualTo(clear.StationHull.Count + 1),
                "A non-station grid intersecting the hull should be visible in the preview");
            Assert.That(obstructed.StationHull.Last().All(point => point.X is > 5 and < 6 && point.Y is > 0 and < 1), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false, false, TestName = "ParkingEvacuatesNestedPassengerPausesShipAndPaysOfflineOwnerAtRoundEnd")]
    [TestCase(true, false, TestName = "RecalledShuttleWithMoreThan500ExcludedItemsCanUseMatchingStorageSlot")]
    [TestCase(true, true, TestName = "FailedSlotWriteKeepsCargoOnRecalledShipAndRetryReturnsItemsOnce")]
    public async Task ParkingRoundTrip(bool saveToStorage, bool failFirstWrites)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan; var maps = entities.System<SharedMapSystem>();
            var map = maps.CreateMap(out var id); var stationGrid = maps.CreateGridEntity(id); var ship = maps.CreateGridEntity(id);
            entities.EnsureComponent<ShuttleComponent>(ship.Owner);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var x=0;x<3;x++) for(var y=0;y<3;y++) { maps.SetTile(stationGrid,new Vector2i(x,y),floor); maps.SetTile(ship,new Vector2i(x,y),floor); }
            if (saveToStorage)
            {
                for (var x = 3; x < 33; x++)
                    maps.SetTile(ship, new Vector2i(x, 1), floor);
            }
            var station = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            entities.EnsureComponent<StationDataComponent>(station);
            entities.System<StationSystem>().AddGridToStation(station,stationGrid.Owner);
            var ownerStation = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            entities.EnsureComponent<StationDataComponent>(ownerStation);
            entities.System<StationSystem>().AddGridToStation(ownerStation,ship.Owner);
            var console = entities.SpawnEntity("ComputerShuttleParking",new EntityCoordinates(stationGrid.Owner,new Vector2(.5f)));
            var first = entities.SpawnEntity("AirlockShuttle",new EntityCoordinates(stationGrid.Owner,new Vector2(2.5f,1.5f)));
            var second = entities.SpawnEntity("AirlockShuttle",new EntityCoordinates(ship.Owner,new Vector2(.5f,1.5f)));
            entities.System<SharedTransformSystem>().SetLocalRotation(first, Angle.FromDegrees(90));
            entities.System<SharedTransformSystem>().SetLocalRotation(second, Angle.FromDegrees(270));
            entities.System<DockingSystem>().Dock((first,entities.GetComponent<DockingComponent>(first)),(second,entities.GetComponent<DockingComponent>(second)));
            var bag = entities.SpawnEntity("ClothingBackpack",new EntityCoordinates(ship.Owner,Vector2.One));
            var body = entities.SpawnEntity("MobHuman",new EntityCoordinates(ship.Owner,Vector2.One));
            var suit = saveToStorage
                ? entities.SpawnEntity("ClothingOuterSuitRad", new EntityCoordinates(ship.Owner, new Vector2(1.5f)))
                : EntityUid.Invalid;
            var lathe = saveToStorage
                ? entities.SpawnEntity("Autolathe", new EntityCoordinates(ship.Owner, new Vector2(2.5f)))
                : EntityUid.Invalid;
            var containers = entities.System<SharedContainerSystem>();
            Assert.That(containers.Insert(body,containers.EnsureContainer<Container>(bag,"passenger")),Is.True);
            var session = pair.Server.PlayerMan.Sessions.Single();
            var parking = entities.System<ParkingSystem>(); parking.RegisterPurchasedShip(ship.Owner,session.UserId.UserId,1,"","Капитан");
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            Assert.Throws<InvalidOperationException>(() => parking.Park(console, ship.Owner), "The port has no breathable landing tile yet");
            Assert.That(entities.GetComponent<TransformComponent>(ship.Owner).MapID,Is.EqualTo(id));
            Assert.That(entities.GetComponent<TransformComponent>(body).ParentUid, Is.EqualTo(bag));
            var moles = new float[Atmospherics.AdjustedNumberOfGases];
            moles[(int) Gas.Oxygen] = 21.82f;
            moles[(int) Gas.Nitrogen] = 82.10f;
            entities.System<AtmosphereSystem>().SetMapAtmosphere(map, false, new GasMixture(moles, Atmospherics.T20C));
            parking.Park(console,ship.Owner);
            var passengerPosition = entities.GetComponent<TransformComponent>(body).Coordinates;
            Assert.That(passengerPosition.EntityId, Is.EqualTo(stationGrid.Owner));
            Assert.That(Vector2.Distance(passengerPosition.Position, new Vector2(2.5f, 1.5f)), Is.InRange(1f, 4f));
            Assert.That(entities.GetComponent<MetaDataComponent>(body).EntityPaused, Is.False);
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            entities.DeleteEntity(body);
            Assert.That(entities.GetComponent<ParkingShuttleComponent>(ship.Owner).Parked,Is.True);
            Assert.That(entities.GetComponent<TransformComponent>(ship.Owner).MapID,Is.Not.EqualTo(id));
            Assert.That(entities.GetComponent<MetaDataComponent>(ship.Owner).EntityPaused,Is.True);
            Assert.That(entities.GetComponent<MetaDataComponent>(bag).EntityPaused,Is.True);
            Assert.That(entities.GetComponent<DockingComponent>(first).DockedWith,Is.Null);
            Assert.That(entities.HasComponent<StationMemberComponent>(ship.Owner), Is.False, "Parked ships cannot remain late-join targets");
            var blockedPort = entities.SpawnEntity("AirlockShuttle", new EntityCoordinates(stationGrid.Owner, new Vector2(1.5f, 1.5f)));
            var transforms = entities.System<SharedTransformSystem>();
            transforms.SetLocalRotation(blockedPort, Angle.FromDegrees(90));
            var recallMap = (ParkingRecallMap) typeof(ParkingSystem).GetMethod("BuildRecallMap", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(parking, [console, ship.Owner])!;
            Assert.That(recallMap.StationHull, Is.Not.Empty);
            Assert.That(recallMap.ShuttleHull, Is.Not.Empty);
            var compatible = recallMap.Ports.Single(port => port.Entity == entities.GetNetEntity(first));
            var incompatible = recallMap.Ports.Single(port => port.Entity == entities.GetNetEntity(blockedPort));
            Assert.That(compatible.Status, Is.EqualTo(ParkingPortStatus.Fits));
            Assert.That(incompatible.Status, Is.EqualTo(ParkingPortStatus.Blocked));
            Assert.That(incompatible.ShipOrigin, Is.Not.Null, "A rejected berth still has a preview pose");
            using (var stream = new MemoryStream())
            {
                var serializer = pair.Server.ResolveDependency<IRobustSerializer>();
                serializer.SerializeDirect(stream, new ParkingUpdate { RecallMap = recallMap });
                stream.Position = 0;
                serializer.DeserializeDirect<ParkingUpdate>(stream, out var decoded);
                Assert.That(decoded.RecallMap!.Ports, Is.EqualTo(recallMap.Ports));
                Assert.That(decoded.RecallMap.ShuttleHull, Is.EqualTo(recallMap.ShuttleHull));
            }
            var privateMap = entities.GetComponent<TransformComponent>(ship.Owner).MapUid!.Value;
            Assert.That(entities.HasComponent<FTLDestinationComponent>(privateMap), Is.False);
            Assert.That(entities.System<Content.Shared._DeepLagoon.Apartments.ApartmentTeleportSystem>().CanTeleport(console, new EntityCoordinates(ship.Owner, Vector2.Zero)), Is.False);
            var copy = parking.LoadSnapshot(parking.SerializeSnapshot(ship.Owner));
            Assert.That(entities.System<DockingSystem>().GetDocks(copy).Count, Is.EqualTo(1));
            Assert.That(entities.System<DockingSystem>().GetDocks(copy).All(d => d.Comp.DockedWith == null), Is.True);
            entities.DeleteEntity(copy);
            var actor = entities.SpawnEntity("MobHuman", new EntityCoordinates(stationGrid.Owner,new Vector2(.5f,1.5f)));
            pair.Server.PlayerMan.SetAttachedEntity(session,actor);
            var card = entities.SpawnEntity("PassengerIDCard",new EntityCoordinates(stationGrid.Owner,Vector2.One));
            entities.GetComponent<Content.Shared.Access.Components.IdCardComponent>(card).FullName = entities.GetComponent<MetaDataComponent>(actor).EntityName;
            entities.System<ShipyardSystem>().BindParkingDeed(ship.Owner,card,"Тестовый шаттл","Капитан");
            Assert.That(containers.Insert(card,entities.GetComponent<ParkingConsoleComponent>(console).Card.ContainerSlot!),Is.True);
            entities.GetComponent<Content.Server.Power.Components.ApcPowerReceiverComponent>(console).Powered=true;
            Assert.That(typeof(ParkingSystem).GetMethod("GetContext",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(parking,[console,actor]),Is.Not.Null,"Fixture must authenticate an inserted ID and active character");
            entities.EventBus.RaiseLocalEvent(console,new Content.Shared._DeepLagoon.ShuttleParking.ParkingAction("recall",dock:entities.GetNetEntity(first))
                { Actor=actor,UiKey=Content.Shared._DeepLagoon.ShuttleParking.ParkingUiKey.Key });
            Assert.That(entities.GetComponent<ParkingShuttleComponent>(ship.Owner).Parked,Is.False);
            Assert.That(entities.GetComponent<TransformComponent>(ship.Owner).MapID,Is.EqualTo(id));
            Assert.That(entities.GetComponent<MetaDataComponent>(bag).EntityPaused,Is.False);
            Assert.That(entities.GetComponent<StationMemberComponent>(ship.Owner).Station,Is.EqualTo(ownerStation));
            Assert.That(entities.GetComponent<DockingComponent>(first).DockedWith,Is.EqualTo(second));
            Assert.That(transforms.GetWorldPosition(ship.Owner), Is.EqualTo(transforms.GetWorldPosition(stationGrid.Owner) + compatible.ShipOrigin!.Value));
            Assert.That(transforms.GetWorldRotation(ship.Owner).Reduced(), Is.EqualTo(compatible.ShipAngle));
            if (saveToStorage)
            {
                var failureLevel = pair.ServerLogHandler.FailureLevel;
                try
                {
                    pair.ServerLogHandler.FailureLevel = LogLevel.Fatal;
                    SaveRecalledShuttle(entities, parking, ship.Owner, console, actor, first, session.UserId.UserId, suit, lathe, failFirstWrites);
                }
                finally
                {
                    pair.ServerLogHandler.FailureLevel = failureLevel;
                }
                pair.Server.PlayerMan.SetAttachedEntity(session, null);
                return;
            }

            pair.Server.PlayerMan.SetAttachedEntity(session,null);

            var database = pair.Server.ResolveDependency<IServerDbManager>();
            var tasks = pair.Server.ResolveDependency<ITaskManager>();
            var owner = new NetUserId(Guid.NewGuid());
            var setup = database.InitPrefsAsync(owner, new HumanoidCharacterProfile().WithBankBalance(2000), default);
            tasks.BlockWaitOnTask(setup);
            var initial = ((HumanoidCharacterProfile) setup.GetAwaiter().GetResult().SelectedCharacter).BankBalance;
            parking.RegisterPurchasedShip(ship.Owner, owner.UserId, 1, "", "Капитан");
            parking.Park(console, ship.Owner, 0);
            var appraisal = entities.System<ShipyardSystem>().AppraiseParkedShuttle(ship.Owner);
            Assert.That(appraisal, Is.Positive);

            parking.SellParkedShuttles();
            parking.SellParkedShuttles();
            Assert.That(entities.EntityExists(ship.Owner), Is.False);
            Assert.That(entities.HasComponent<ShuttleDeedComponent>(card), Is.False);
            var saved = database.GetPlayerPreferencesAsync(owner, default);
            tasks.BlockWaitOnTask(saved);
            var balance = ((HumanoidCharacterProfile) saved.GetAwaiter().GetResult()!.SelectedCharacter).BankBalance;
            Assert.That(balance, Is.EqualTo(initial + appraisal));
        });
        await pair.CleanReturnAsync();
    }

    private static void SaveRecalledShuttle(IEntityManager entities, ParkingSystem parking, EntityUid ship, EntityUid console,
        EntityUid actor, EntityUid dock, Guid owner, EntityUid suit, EntityUid lathe, bool failFirstWrites)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        const int category = 2;
        const int excludedCount = 543;
        Assert.That(parking.GetCategory(ship), Is.EqualTo(category));
        var store = (ParkingStore) typeof(ParkingSystem).GetField("_store", flags)!.GetValue(parking)!;
        store.GrantPurchase(owner, store.BeginPurchase(owner, Guid.NewGuid(), category));
        var slot = store.Slots(owner).Single();

        for (var i = 0; i < excludedCount; i++)
            entities.SpawnEntity("Claymore", new EntityCoordinates(ship, new Vector2(1.5f, 0.5f)));

        var containers = entities.System<SharedContainerSystem>();
        var hood = containers.GetContainer(suit, ToggleableClothingComponent.DefaultClothingContainerId).ContainedEntities.Single();
        entities.AddComponent<GridEditorBrokenExportComponent>(hood);
        var migrated = entities.SpawnEntity("ChairWood", new EntityCoordinates(ship, new Vector2(1.5f)));
        var lathePosition = entities.GetComponent<TransformComponent>(lathe).LocalPosition;
        var latheComponent = entities.GetComponent<LatheComponent>(lathe);
        latheComponent.Paused = true;
        var prototypes = entities.EntitySysManager.DependencyCollection.Resolve<IPrototypeManager>();
        var crowbar = prototypes.Index(new ProtoId<LatheRecipePrototype>("CrowbarGreen"));
        var screwdriver = prototypes.Index(new ProtoId<LatheRecipePrototype>("Screwdriver"));
        var lathes = entities.System<LatheSystem>();
        Assert.That(lathes.TryAddToQueue(lathe, crowbar, 5, latheComponent, actor, canDebt: true), Is.True);
        Assert.That(lathes.TryAddToQueue(lathe, screwdriver, 3, latheComponent, actor, canDebt: true), Is.True);
        latheComponent.Queue[0].ItemsPrinted = 2;
        var cargo = entities.EntityQuery<TransformComponent>(true).Where(xform => xform.GridUid == ship && xform.Owner != ship)
            .ToDictionary(xform => xform.Owner, xform => xform.ParentUid);
        var mob = entities.SpawnEntity("MobHuman", new EntityCoordinates(ship, new Vector2(1.5f)));
        var inventoryItem = entities.SpawnEntity("Claymore", new EntityCoordinates(ship, new Vector2(1.5f)));
        Assert.That(containers.Insert(inventoryItem, containers.EnsureContainer<Container>(mob, "test-equipment")), Is.True);
        Assert.That(parking.ExcludedEntities(ship), Has.Count.EqualTo(excludedCount));
        entities.EventBus.RaiseLocalEvent(console, new ParkingAction("preview", slot.Id) { Actor = actor, UiKey = ParkingUiKey.Key });
        var reviews = (IDictionary) typeof(ParkingSystem).GetField("_reviews", flags)!.GetValue(parking)!;
        var review = reviews[owner];
        Assert.That(review, Is.Not.Null);
        var token = (Guid) review!.GetType().GetProperty("Token")!.GetValue(review)!;
        var preview = (List<string>) typeof(ParkingSystem).GetMethod("ExclusionPreview", flags)!.Invoke(parking, [ship])!;
        Assert.That(preview.Single(), Does.Contain($"× {excludedCount} (конфискация)"));
        Assert.That(entities.GetComponent<TransformComponent>(mob).GridUid, Is.EqualTo(ship));

        var sound = new SoundPathSpecifier("/Audio/_Goobstation/Ambience/ambilathe1.ogg");
        var audio = entities.System<SharedAudioSystem>().PlayPvs(sound, ship);
        Assert.That(audio, Is.Not.Null);
        Assert.That(entities.GetComponent<TransformComponent>(audio!.Value.Entity).GridUid, Is.EqualTo(ship));
        Assert.That(parking.ExcludedEntities(ship), Does.Not.Contain(audio.Value.Entity));

        var migration = entities.System<ParkingMigrationTestSystem>();
        var consoleGrid = entities.GetComponent<TransformComponent>(console).GridUid;
        EntityUid[] ReturnedItems() => entities.EntityQuery<MetaDataComponent, TransformComponent>(true)
            .Where(entry => entry.Item2.GridUid == consoleGrid && entry.Item1.EntityPrototype?.ID is "ClothingHeadHatHoodRad" or "ChairWood")
            .Select(entry => entry.Item1.Owner).ToArray();

        void SubmitSave()
        {
            try
            {
                migration.DeletedPrototype = "ChairWood";
                migration.LoadsBeforeDeletion = 1;
                entities.EventBus.RaiseLocalEvent(console, new ParkingAction("save", slot.Id, token: token) { Actor = actor, UiKey = ParkingUiKey.Key });
            }
            finally
            {
                migration.DeletedPrototype = null;
                migration.LoadsBeforeDeletion = 0;
            }
        }

        if (failFirstWrites)
        {
            var db = (SqliteConnection) typeof(ParkingStore).GetField("_db", flags)!.GetValue(store)!;
            var rejectedWrites = 0;
            db.CreateFunction("note_rejected_parking_write", () => ++rejectedWrites);
            using var fault = db.CreateCommand();
            fault.CommandText = """
                CREATE TEMP TRIGGER reject_parking_slot_write AFTER UPDATE OF ship ON parking_slots
                WHEN NEW.phase = 'saving' BEGIN
                    SELECT note_rejected_parking_write();
                    SELECT RAISE(ABORT, 'parking test slot write failure');
                END;
                """;
            fault.ExecuteNonQuery();

            void AssertCargoAboard()
            {
                var aboard = entities.EntityQuery<TransformComponent>(true).Where(xform => xform.GridUid == ship && cargo.ContainsKey(xform.Owner))
                    .ToDictionary(xform => xform.Owner, xform => xform.ParentUid);
                Assert.That(aboard, Is.EquivalentTo(cargo));
                Assert.That(ReturnedItems(), Is.Empty);
                Assert.That(entities.HasComponent<AttachedClothingComponent>(hood), Is.True);
            }

            var payoutsBeforeSave = store.PendingPayouts();
            try
            {
                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    SubmitSave();
                    Assert.That(rejectedWrites, Is.EqualTo(attempt), "Each save must reach the SQLite slot update");
                    Assert.That(store.Slot(owner, slot.Id), Is.EqualTo(slot));
                    Assert.That(store.PendingPayouts(), Is.EquivalentTo(payoutsBeforeSave));
                    Assert.That(entities.GetComponent<ParkingShuttleComponent>(ship).Parked, Is.True);
                    AssertCargoAboard();
                }

                entities.EventBus.RaiseLocalEvent(console, new ParkingAction("recall", dock: entities.GetNetEntity(dock))
                    { Actor = actor, UiKey = ParkingUiKey.Key });
                Assert.That(entities.GetComponent<ParkingShuttleComponent>(ship).Parked, Is.False);
                Assert.That(entities.GetComponent<DockingComponent>(dock).DockedWith, Is.Not.Null);
                Assert.That(entities.GetComponent<MetaDataComponent>(ship).EntityPaused, Is.False);
                AssertCargoAboard();
            }
            finally
            {
                fault.CommandText = "DROP TRIGGER reject_parking_slot_write;";
                fault.ExecuteNonQuery();
            }
        }

        var timing = entities.EntitySysManager.DependencyCollection.Resolve<IGameTiming>();
        var savedAfter = timing.RealTime;
        SubmitSave();
        var saved = store.Slot(owner, slot.Id);
        Assert.That(saved.Phase, Is.EqualTo("stored"));
        Assert.That(saved.Ship?.Category, Is.EqualTo(category));
        Assert.That(saved.Ship!.SavedShift, Is.Not.EqualTo(Guid.Empty));
        Assert.That(saved.Ship.FreeReturnAt, Is.InRange(savedAfter + ParkingRules.FreeReturnDelay, timing.RealTime + ParkingRules.FreeReturnDelay));
        Assert.That(entities.EntityExists(ship), Is.False);
        Assert.That(entities.GetComponent<TransformComponent>(mob).GridUid, Is.EqualTo(entities.GetComponent<TransformComponent>(dock).GridUid));
        Assert.That(entities.GetComponent<TransformComponent>(inventoryItem).ParentUid, Is.EqualTo(mob));
        Assert.That(entities.GetComponent<TransformComponent>(hood).GridUid, Is.EqualTo(consoleGrid));
        Assert.That(entities.GetComponent<TransformComponent>(migrated).GridUid, Is.EqualTo(consoleGrid));
        Assert.That(entities.HasComponent<AttachedClothingComponent>(hood), Is.False);
        Assert.That(entities.IsQueuedForDeletion(hood), Is.False);
        if (failFirstWrites)
        {
            var payoutsAfterSave = store.PendingPayouts();
            SubmitSave();
            Assert.That(store.Slot(owner, slot.Id), Is.EqualTo(saved));
            Assert.That(store.PendingPayouts(), Is.EquivalentTo(payoutsAfterSave));
            Assert.That(ReturnedItems(), Is.EquivalentTo(new[] { hood, migrated }));
        }
        var restored = parking.LoadSnapshot(saved.Ship!.Grid);
        Assert.That(parking.GetCategory(restored), Is.EqualTo(category));
        Assert.That(parking.ExcludedEntities(restored), Is.Empty);
        Assert.That(entities.EntityQuery<AudioComponent, TransformComponent>(true)
            .Any(entry => entry.Item2.GridUid == restored), Is.False);
        var restoredPrototypes = entities.EntityQuery<MetaDataComponent, TransformComponent>(true)
            .Where(entry => entry.Item2.GridUid == restored).Select(entry => entry.Item1.EntityPrototype?.ID).ToHashSet();
        Assert.That(restoredPrototypes, Does.Contain("ClothingOuterSuitRad"));
        Assert.That(restoredPrototypes, Does.Not.Contain("ClothingHeadHatHoodRad").And.Not.Contain("ChairWood"));
        entities.DeleteEntity(restored);

        entities.AddComponent<IronmanComponent>(actor);
        entities.EventBus.RaiseLocalEvent(console, new ParkingAction("redeem", slot.Id, dock: entities.GetNetEntity(dock))
            { Actor = actor, UiKey = ParkingUiKey.Key });
        Assert.That(store.Slot(owner, slot.Id), Is.EqualTo(saved), "Early return must leave the slot untouched");

        // Move only the stored deadline to exercise the real console return without waiting five minutes.
        var connection = (SqliteConnection) typeof(ParkingStore).GetField("_db", flags)!.GetValue(store)!;
        using var deadline = connection.CreateCommand();
        deadline.CommandText = "UPDATE parking_slots SET ship=$ship WHERE id=$id AND owner=$owner";
        deadline.Parameters.AddWithValue("$ship", JsonSerializer.Serialize(saved.Ship with { FreeReturnAt = timing.RealTime }));
        deadline.Parameters.AddWithValue("$id", slot.Id.ToString());
        deadline.Parameters.AddWithValue("$owner", owner.ToString());
        Assert.That(deadline.ExecuteNonQuery(), Is.EqualTo(1));

        entities.EventBus.RaiseLocalEvent(console, new ParkingAction("redeem", slot.Id, dock: entities.GetNetEntity(dock))
            { Actor = actor, UiKey = ParkingUiKey.Key });
        Assert.That(store.Slot(owner, slot.Id).Phase, Is.EqualTo("empty"));
        var card = entities.GetComponent<ParkingConsoleComponent>(console).Card.Item!.Value;
        var delivered = entities.GetComponent<ShuttleDeedComponent>(card).ShuttleUid!.Value;
        Assert.That(entities.GetComponent<MetaDataComponent>(delivered).EntityPaused, Is.False);
        Assert.That(entities.GetComponent<DockingComponent>(dock).DockedWith, Is.Not.Null);
        Assert.That(ReturnedItems(), Is.EquivalentTo(new[] { hood, migrated }));

        Assert.That(entities.EntityExists(lathe), Is.False);
        var (returnedLathe, transform) = entities.EntityQuery<LatheComponent, TransformComponent>(true)
            .Single(entry => entry.Item2.GridUid == delivered);
        Assert.That(entities.GetComponent<MetaDataComponent>(returnedLathe.Owner).EntityPrototype?.ID, Is.EqualTo("Autolathe"));
        Assert.That(transform.Coordinates, Is.EqualTo(new EntityCoordinates(delivered, lathePosition)));
        Assert.That(returnedLathe.Paused, Is.True);
        Assert.That(returnedLathe.Queue.Select(batch => (batch.Recipe, batch.ItemsPrinted, batch.ItemsRequested)),
            Is.EqualTo(new[] { (crowbar, 2, 5), (screwdriver, 0, 3) }));
        Assert.That(returnedLathe.Queue.Select(batch => batch.Actor), Is.All.Null);
        Assert.That(lathes.TryAddToQueue(returnedLathe.Owner, crowbar, 1, returnedLathe, actor, canDebt: true), Is.True);
        Assert.That(returnedLathe.Queue.Select(batch => batch.Index), Is.Unique);
    }

    [Test]
    public async Task SuppliedCapitalMapCanRoundTripThroughPersistentSerializer()
    {
        var path = Environment.GetEnvironmentVariable("DL_PARKING_CAPITAL_MAP");
        if (string.IsNullOrEmpty(path)) Assert.Ignore("Set DL_PARKING_CAPITAL_MAP to inspect a supplied capital grid.");
        var text = File.ReadAllText(path!);
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan; var maps = entities.System<SharedMapSystem>(); maps.CreateMap(out var id);
            using var reader = new StringReader(text);
            Assert.That(entities.System<MapLoaderSystem>().TryLoadGrid(id,reader,"capital parking fixture",out var loaded),Is.True);
            var grid = loaded!.Value.Owner;
            var parking = entities.System<ParkingSystem>(); parking.RegisterPurchasedShip(grid,Guid.NewGuid(),1000000,"","Капитан");
            Assert.That(parking.GetCategory(grid),Is.EqualTo(4));
            var snapshot = parking.SerializeSnapshot(grid);
            var copy = parking.LoadSnapshot(snapshot);
            Assert.That(parking.GetCategory(copy),Is.EqualTo(4));
            Assert.That(maps.GetAllTiles(copy,entities.GetComponent<MapGridComponent>(copy)).Count(),Is.EqualTo(maps.GetAllTiles(grid,entities.GetComponent<MapGridComponent>(grid)).Count()));
            foreach (var reference in new[] { ("duran",1), ("pelican",2) })
            {
                Assert.That(entities.System<MapLoaderSystem>().TryLoadGrid(id,new Robust.Shared.Utility.ResPath("/SharedMaps/_Mono/Shuttles/CivilianExp/"+reference.Item1+".yml"),out var referenceGrid),Is.True);
                Assert.That(parking.GetCategory(referenceGrid!.Value.Owner),Is.EqualTo(reference.Item2),reference.Item1);
            }
        });
        await pair.CleanReturnAsync();
    }
    [Test]
    public async Task SnapshotRetainsOriginalEquipmentDecalsAndLocalTransformsAndRebindsNewCard()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var transform = entities.System<SharedTransformSystem>();
            var meta = entities.System<MetaDataSystem>();
            var containers = entities.System<SharedContainerSystem>();
            var parking = entities.System<ParkingSystem>();
            var map = maps.CreateMap(out var id);
            var grid = maps.CreateGridEntity(id);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var x = 0; x < 6; x++) for (var y = 0; y < 6; y++) maps.SetTile(grid, new Vector2i(x,y), floor);
            transform.SetCoordinates(grid.Owner, new EntityCoordinates(map, new Vector2(123, -57)));
            transform.SetLocalRotation(grid.Owner, Angle.FromDegrees(31));
            var helm = entities.SpawnEntity("ComputerShuttle", new EntityCoordinates(grid.Owner, new Vector2(1.5f)));
            var oldCard = entities.SpawnEntity("PassengerIDCard", new EntityCoordinates(map, Vector2.Zero));
            entities.System<ShipyardSystem>().BindParkingDeed(grid.Owner, oldCard, "Исходный шаттл", "Капитан");
            var originalGun = entities.SpawnEntity("WeaponPistolMk58", new EntityCoordinates(grid.Owner, new Vector2(2.5f)));
            meta.SetEntityName(originalGun, "original weapon");
            var originalBag = entities.SpawnEntity("ClothingBackpack", new EntityCoordinates(grid.Owner, new Vector2(3.5f)));
            var originalBeaker = entities.SpawnEntity("Beaker", new EntityCoordinates(grid.Owner, new Vector2(4.5f)));
            meta.SetEntityName(originalBeaker, "original beaker");
            var removedOriginal = entities.SpawnEntity("Wrench", new EntityCoordinates(grid.Owner, Vector2.One));
            meta.SetEntityName(removedOriginal, "removed original item");
            // Paper contains a food solution and is covered by the safe blacklist; only stock paper is exempt.
            var paper = entities.SpawnEntity("Paper", new EntityCoordinates(grid.Owner, new Vector2(1.25f, 2.75f)));
            meta.SetEntityName(paper, "saved loose note");
            parking.RegisterPurchasedShip(grid.Owner, Guid.NewGuid(), 81733, "", "Капитан");
            transform.SetCoordinates(removedOriginal, new EntityCoordinates(map, new Vector2(90)));
            var addedGun = entities.SpawnEntity("WeaponPistolMk58", new EntityCoordinates(grid.Owner, Vector2.One));
            meta.SetEntityName(addedGun, "added weapon");
            // A clone can copy component data. Its runtime identity still must not become original equipment.
            var copiedStamp = entities.EnsureComponent<ParkingOriginalComponent>(addedGun);
            var realStamp = entities.GetComponent<ParkingOriginalComponent>(originalGun);
            copiedStamp.Shuttle = realStamp.Shuttle; copiedStamp.Identity = realStamp.Identity;
            var addedBeaker = entities.SpawnEntity("Beaker", new EntityCoordinates(grid.Owner, Vector2.One));
            var melee = new[] { "EnergySword", "Sledgehammer", "BaseBallBat", "Claymore" }
                .Select(proto => entities.SpawnEntity(proto, new EntityCoordinates(grid.Owner, Vector2.One))).ToArray();
            var addedBag = entities.SpawnEntity("ClothingBackpack", new EntityCoordinates(grid.Owner, new Vector2(3.5f)));
            var slot = containers.EnsureContainer<Container>(originalBag, "parking-test");
            Assert.That(containers.Insert(addedBag, slot), Is.True);
            var inner = containers.EnsureContainer<Container>(addedBag, "parking-test");
            Assert.That(containers.Insert(originalGun, inner), Is.True);
            var nested = entities.SpawnEntity("Wrench", new EntityCoordinates(grid.Owner, Vector2.One));
            Assert.That(containers.Insert(nested, inner), Is.True);
            entities.GetComponent<PaperComponent>(paper).Content = "Saved loose note";
            transform.SetLocalRotation(paper, Angle.FromDegrees(37));
            var wrench = entities.SpawnEntity("Wrench", new EntityCoordinates(grid.Owner, new Vector2(2.25f, 2.75f)));
            var decals = entities.System<DecalSystem>();
            Assert.That(decals.TryAddDecal("heart", new EntityCoordinates(grid.Owner, new Vector2(2,2)), out _, Color.Blue, Angle.FromDegrees(45), 4, true), Is.True);
            var excluded = parking.ExcludedEntities(grid.Owner);
            Assert.That(excluded, Does.Contain(addedGun)); Assert.That(excluded, Does.Contain(addedBeaker));
            foreach (var weapon in melee) Assert.That(excluded, Does.Contain(weapon),entities.GetComponent<MetaDataComponent>(weapon).EntityPrototype!.ID);
            Assert.That(excluded, Does.Contain(addedBag)); Assert.That(excluded, Does.Contain(nested));
            Assert.That(excluded, Does.Not.Contain(originalGun)); Assert.That(excluded, Does.Not.Contain(originalBeaker));
            Assert.That(excluded, Does.Not.Contain(wrench));
            Assert.That(excluded, Does.Not.Contain(paper));
            var snapshot = parking.FilteredSnapshot(grid.Owner);
            Assert.That(snapshot, Does.Not.Contain("proto: PassengerIDCard"), "External card references must not enter the snapshot");
            // Preparing a snapshot must not remove or reposition the live ship's objects.
            Assert.That(entities.EntityExists(addedGun), Is.True);
            Assert.That(containers.GetContainer(addedBag, "parking-test").Contains(originalGun), Is.True);
            var restored = parking.LoadSnapshot(snapshot);
            Assert.That(entities.GetComponent<ParkingShuttleComponent>(restored).OwnerAccount, Is.EqualTo(entities.GetComponent<ParkingShuttleComponent>(grid.Owner).OwnerAccount));
            Assert.That(entities.GetComponent<ParkingShuttleComponent>(restored).OriginalPrice, Is.EqualTo(81733));
            var descendants = entities.GetComponent<TransformComponent>(restored).ChildEnumerator;
            var all = new System.Collections.Generic.List<EntityUid>();
            void Collect(EntityUid parent)
            {
                var children = entities.GetComponent<TransformComponent>(parent).ChildEnumerator;
                while (children.MoveNext(out var child)) { all.Add(child); Collect(child); }
            }
            Collect(restored);
            Assert.That(all.Count(e => entities.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID == "WeaponPistolMk58"), Is.EqualTo(1));
            Assert.That(all.Any(e => entities.GetComponent<MetaDataComponent>(e).EntityName == "original weapon"), Is.True);
            Assert.That(all.Any(e => entities.GetComponent<MetaDataComponent>(e).EntityName == "added weapon"), Is.False);
            Assert.That(all.Any(e => entities.GetComponent<MetaDataComponent>(e).EntityName == "removed original item"), Is.False, "An original item taken off the ship must not be duplicated back aboard");
            var restoredPaper = all.Single(e => entities.GetComponent<MetaDataComponent>(e).EntityName == "saved loose note");
            Assert.That(entities.GetComponent<PaperComponent>(restoredPaper).Content, Is.EqualTo("Saved loose note"));
            var position = entities.GetComponent<TransformComponent>(restoredPaper);
            Assert.That(position.LocalPosition, Is.EqualTo(new Vector2(1.25f,2.75f)));
            Assert.That(position.LocalRotation.Degrees, Is.EqualTo(37).Within(.001));
            var restoredDecal = decals.GetDecalsInRange(restored, new Vector2(2.5f), .1f).Single().Decal;
            Assert.That(restoredDecal.Color, Is.EqualTo(Color.Blue));
            Assert.That(restoredDecal.Angle.Degrees, Is.EqualTo(45).Within(.001));
            Assert.That(restoredDecal.ZIndex, Is.EqualTo(4)); Assert.That(restoredDecal.Cleanable, Is.True);
            var newCard = entities.SpawnEntity("PassengerIDCard", new EntityCoordinates(map, Vector2.Zero));
            entities.System<ShipyardSystem>().BindParkingDeed(restored, newCard, "Перестроенный шаттл", "Капитан");
            Assert.That(entities.GetComponent<ShuttleDeedComponent>(newCard).ShuttleUid, Is.EqualTo(restored));
            Assert.That(entities.GetComponent<ShuttleDeedComponent>(restored).DeedHolder, Is.EqualTo(newCard));
            var restoredHelm = all.Single(e => entities.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID == "ComputerShuttle");
            Assert.That(entities.GetComponent<ShuttleConsoleLockComponent>(restoredHelm).ShuttleId, Is.EqualTo(restored.ToString()));
        });
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var panel = new NativeInterfacePanel((_,_) => { }, () => { });
            ui.StateRoot.AddChild(panel);
            try
            {
                var data = new TguiData().Bool("id",true).Bool("hasShip",true).String("user","Александр Константинович Северин · CaptainSeverin")
                    .String("shipName","Межсекторный экспедиционный шаттл с очень длинным русским названием").String("owner","Александр Константинович Северин")
                    .String("category","Средний").Number("shipCategory",1).Number("credits",800000).String("coins","1480")
                    .String("description",Content.Shared._DeepLagoon.ShuttleParking.ParkingRules.StorageDescription)
                    .String("buyToken",Guid.NewGuid().ToString()).String("message","Длинное русское сообщение о результате операции")
                    .Array("docks",new[]{new TguiData().String("id","101").String("name","Шлюз №3 · свободен")})
                    .Array("places",new[]{new TguiData().String("id",Guid.NewGuid().ToString()).Number("category",1).String("categoryName","Средний").String("name","")})
                    .Array("categories",Enumerable.Range(0,5).Select(i=>new TguiData().Number("id",i).String("name",Content.Shared._DeepLagoon.ShuttleParking.ParkingRules.CategoryName(i)).Number("count",0).Number("first",1000).Number("extra",500)));
                var tab = typeof(NativeInterfacePanel).GetField("_parkingTab",BindingFlags.NonPublic|BindingFlags.Instance)!;
                for(var i=0;i<3;i++)
                {
                    tab.SetValue(panel,i); panel.UpdateState("ShuttleParking",data.ToString());
                    panel.Measure(new Vector2(280,600)); panel.Arrange(UIBox2.FromDimensions(Vector2.Zero,new Vector2(280,600)));
                    void Check(Control control)
                    {
                        if (control.Visible && control is Button) Assert.That(control.DesiredSize.X,Is.LessThanOrEqualTo(280),"Button caption must fit the native minimum width");
                        foreach(var child in control.Children) Check(child);
                    }
                    Check(panel);
                }
            }
            finally { ui.StateRoot.RemoveChild(panel); panel.Dispose(); }
        });
        await pair.CleanReturnAsync();
    }
}

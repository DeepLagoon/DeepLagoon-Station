using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.Apartments;
using Content.Server._DeepLagoon.Apartments;
using Content.Server.Atmos.EntitySystems;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Atmos;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentAtmosphereTests
{
    [Test]
    public async Task ControllerRegulatesOnlyItsApartmentAndRejectsInvalidSettings()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        EntityUid map = default, apartment = default, neighbor = default, ordinary = default;
        EntityUid controller = default, duplicate = default, outside = default, actor = default;
        void AssertAir(EntityUid grid, int oxygen)
        {
            var entities = pair.Server.EntMan;
            var atmosphere = entities.System<AtmosphereSystem>();
            var tiles = entities.System<SharedMapSystem>().GetAllTiles(grid, entities.GetComponent<MapGridComponent>(grid)).ToArray();
            Assert.That(tiles, Is.Not.Empty);
            foreach (var tile in tiles)
            {
                var air = atmosphere.GetTileMixture(grid, map, tile.GridIndices)!;
                Assert.That(air.Immutable, Is.True);
                Assert.That(air.Pressure, Is.EqualTo(Atmospherics.OneAtmosphere).Within(.01));
                Assert.That(air.Temperature, Is.EqualTo(Atmospherics.T20C).Within(.01));
                Assert.That(air.GetMoles(Gas.Oxygen) / air.TotalMoles * 100, Is.EqualTo(oxygen).Within(.001));
                Assert.That(air.GetMoles(Gas.Nitrogen) / air.TotalMoles * 100, Is.EqualTo(100 - oxygen).Within(.001));
                for (var gas = 0; gas < Atmospherics.TotalNumberOfGases; gas++)
                {
                    if ((Gas) gas is Gas.Oxygen or Gas.Nitrogen)
                        continue;
                    Assert.That(air.GetMoles(gas), Is.Zero);
                    air.AdjustMoles(gas, 100);
                    Assert.That(air.GetMoles(gas), Is.Zero);
                }
            }
        }
        void Set(EntityUid device, int percent) => pair.Server.EntMan.EventBus.RaiseLocalEvent(device,
            new ApartmentAtmosphereSetMessage(percent) { Actor = actor, UiKey = ApartmentAtmosphereUiKey.Key });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            map = maps.CreateMap(out var mapId, runMapInit: true);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            EntityUid Grid(bool home, int offset)
            {
                var grid = maps.CreateGridEntity(mapId);
                entities.System<SharedTransformSystem>().SetLocalPosition(grid, new Vector2(offset, 0));
                maps.SetTiles(grid, grid.Comp, new[] { (Vector2i.Zero, floor), (new Vector2i(1, 0), floor), (new Vector2i(0, 1), floor), (Vector2i.One, floor) }.ToList());
                if (home) entities.AddComponent<ApartmentGridComponent>(grid);
                entities.System<AtmosphereSystem>().SetSafeGridAtmosphere(grid);
                return grid;
            }
            apartment = Grid(true, 0);
            neighbor = Grid(true, 10);
            ordinary = Grid(false, 20);
            controller = entities.SpawnEntity("DLApartmentAtmosphereController", new EntityCoordinates(apartment, new Vector2(.5f, .5f)));
            duplicate = entities.SpawnEntity("DLApartmentAtmosphereController", new EntityCoordinates(apartment, new Vector2(1.5f, 1.5f)));
            outside = entities.SpawnEntity("DLApartmentAtmosphereController", new EntityCoordinates(ordinary, new Vector2(.5f, .5f)));
            actor = entities.SpawnEntity(null, new EntityCoordinates(apartment, new Vector2(.5f, .5f)));
            var transforms = entities.System<SharedTransformSystem>();
            Assert.That(transforms.AnchorEntity(controller), Is.True);
            Assert.That(transforms.AnchorEntity(duplicate), Is.True);
            Assert.That(transforms.AnchorEntity(outside), Is.True);
            foreach (var percent in new[] { 0, 100, 30, 21 })
            {
                Set(controller, percent);
                AssertAir(apartment, percent);
                AssertAir(neighbor, 21);
                AssertAir(ordinary, 21);
            }
            Set(duplicate, 45);
            Set(controller, -1);
            Set(controller, 101);
            AssertAir(apartment, 45);
            Set(outside, 80);
            Assert.That(entities.HasComponent<ApartmentAtmosphereSettingsComponent>(ordinary), Is.False);
            entities.System<SharedTransformSystem>().SetCoordinates(actor, new EntityCoordinates(neighbor, new Vector2(.5f, .5f)));
            Set(controller, 90);
            AssertAir(apartment, 45);
        });
        await pair.RunTicksSync(90);
        await pair.Server.WaitAssertion(() =>
        {
            AssertAir(apartment, 45);
            AssertAir(neighbor, 21);
            AssertAir(ordinary, 21);
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.SetTile(apartment, entities.GetComponent<MapGridComponent>(apartment), new Vector2i(2, 0), new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId));
            entities.System<AtmosphereSystem>().SetSafeGridAtmosphere(apartment);
            AssertAir(apartment, 45);
            entities.DeleteEntity(controller);
            entities.DeleteEntity(duplicate);
        });
        await pair.RunTicksSync(90);
        await pair.Server.WaitAssertion(() => AssertAir(apartment, 21));
        await pair.Client.WaitAssertion(() =>
        {
            using var window = new ApartmentAtmosphereWindow();
            window.SetState(new ApartmentAtmosphereState(30, true));
            Assert.That(window.Oxygen.Value, Is.EqualTo(30));
            window.Oxygen.Value = 40;
            window.SetState(new ApartmentAtmosphereState(30, true));
            Assert.That(window.Oxygen.Value, Is.EqualTo(40), "Periodic updates must preserve an unfinished edit.");
            window.SetState(new ApartmentAtmosphereState(30, false));
            Assert.That(window.Apply.Disabled, Is.True);
            Assert.That(window.Oxygen.LineEditDisabled, Is.True);
            Assert.That(window.Oxygen.Children.OfType<Robust.Client.UserInterface.Controls.Button>().All(button => button.Disabled), Is.True);
        });
        await pair.CleanReturnAsync();
    }
    [Test]
    public async Task ManagedApartmentKeepsCompositionAfterLayoutSaveAndUnload()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        await pair.Server.WaitPost(() => pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true));
        var testMap = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var entities = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            var system = entities.System<ApartmentSystem>();
            var body = entities.SpawnEntity("MobHuman", testMap.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            Assert.That(system.Enter(session, session.UserId, out var message), Is.True, message);
            var home = system.Instances[session.UserId];
            Assert.That(system.SaveAtmosphereSettings(home.Grid, 35), Is.True);
            Assert.That(home.Delta.OxygenPercent, Is.EqualTo(35));
            var token = Guid.NewGuid();
            var editors = (System.Collections.Generic.Dictionary<Robust.Shared.Network.NetUserId, Guid>)
                typeof(ApartmentSystem).GetField("_editors", flags)!.GetValue(system)!;
            editors[session.UserId] = token;
            var layout = (System.Collections.Generic.List<ApartmentPlacement>)
                typeof(ApartmentSystem).GetMethod("CurrentLayout", flags)!.Invoke(system, new object[] { home })!;
            Assert.That(system.Apply(session, token, home.Delta.Revision, layout), Is.Null);
            Assert.That(home.Delta.OxygenPercent, Is.EqualTo(35));
            Assert.That(system.Leave(session.UserId, out message), Is.True, message);
            home.IdleSince = TimeSpan.FromSeconds(-10000);
            typeof(ApartmentSystem).GetField("_nextUpdate", flags)!.SetValue(system, TimeSpan.Zero);
            system.Update(1);
            Assert.That(system.Instances.ContainsKey(session.UserId), Is.False);
            Assert.That(system.Enter(session, session.UserId, out message), Is.True, message);
            var reopened = system.Instances[session.UserId];
            Assert.That(reopened.Grid, Is.Not.EqualTo(home.Grid));
            Assert.That(reopened.Delta.OxygenPercent, Is.EqualTo(35));
            Assert.That(entities.GetComponent<ApartmentAtmosphereSettingsComponent>(reopened.Grid).OxygenPercent, Is.EqualTo(35));
            Assert.That(system.Leave(session.UserId, out message), Is.True, message);
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            entities.DeleteEntity(body);
        });
        await pair.CleanReturnAsync();
    }
}

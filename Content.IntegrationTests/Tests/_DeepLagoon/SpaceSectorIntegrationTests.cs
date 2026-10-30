using System;
using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.SpaceEnvironment;
using Content.Server.Power.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared.Access.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.Maps;
using Content.Server.Radio;
using Content.Server.Radiation.Components;
using Content.Server.Radiation.Events;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared._FarHorizons.StarSystem;
using Content.Shared._FarHorizons.StarSystem.Prototypes;
using Robust.Shared.Configuration;
using Robust.Client.Graphics;
using Robust.Shared.Map;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using StarMapSystem = Content.Server._FarHorizons.StarSystem.StarSystemMapSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class SpaceSectorIntegrationTests
{
    [Test]
    public async Task ProfilesBuildValidPlanetVisualsAndRebindingDoesNotDuplicateLandmarks()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var shaders = new System.Collections.Generic.HashSet<string>();
        var portraits = new System.Collections.Generic.List<(Content.Shared._FarHorizons.StarSystem.Helpers.Planet Planet,
            Content.Shared._FarHorizons.StarSystem.Helpers.Star Star)>();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var cfg = pair.Server.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(SpaceEnvironmentCVars.Campaign, "integration-sector-profiles");
            var sectors = entities.System<SectorSystem>();
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            foreach (var profile in prototypes.EnumeratePrototypes<SectorProfilePrototype>())
            {
                var map = sectors.Create("test/" + profile.ID, 719, profile.ID);
                var star = entities.GetComponent<StarSystemMapComponent>(map);
                Assert.That(star.StarSystem, Is.Not.Null);
                Assert.That(star.StarSystem.Planets, Has.Count.InRange(2, 8));
                foreach (var planet in star.StarSystem.Planets)
                {
                    Assert.That(prototypes.HasIndex<PlanetPalettePrototype>(planet.Palette), Is.True);
                    shaders.Add(planet.Shader);
                    portraits.Add((planet, star.StarSystem.Star));
                }
                int Count()
                {
                    var count = 0;
                    var query = entities.EntityQueryEnumerator<SectorLandmarkComponent, TransformComponent>();
                    while (query.MoveNext(out _, out _, out var transform))
                        if (transform.ParentUid == map) count++;
                    return count;
                }
                var before = Count();
                sectors.Bind(map, star.Definition!);
                Assert.That(Count(), Is.EqualTo(before));
                entities.DeleteEntity(map);
            }
        });
        await pair.Client.WaitAssertion(() =>
        {
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            foreach (var id in shaders) Assert.That(prototypes.Index<ShaderPrototype>(id), Is.Not.Null);
            foreach (var (planet, star) in portraits)
            {
                using var portrait = Content.Client._FarHorizons.StarSystem.PlanetShaderFactory.Create(prototypes, planet, star);
                Assert.That(portrait, Is.Not.Null, planet.Name);
                portrait.SetParameter("planetaryRadiusFactor", 1f);
                portrait.SetParameter("viewportMin", -Vector2.One);
                portrait.SetParameter("viewportSize", Vector2.One * 2);
            }
            using var cloud = prototypes.Index<ShaderPrototype>("DeepLagoonSpaceCloud").InstanceUnique();
            cloud.SetParameter("cloudDensity", 0.5f);
            cloud.SetParameter("cloudSeed", 1f);
            cloud.SetParameter("cloudColor", Color.Cyan);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExistingKyphrusKeepsAllNamedPlanetsAndFtlUsesActualEllipse()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var map = maps.CreateMap(out _);
            var stars = entities.EnsureComponent<StarSystemMapComponent>(map);
            entities.System<StarMapSystem>().SetSystem((map, stars), "SystemKyphrus");
            Assert.That(stars.StarSystem!.Planets.Select(p => p.Name),
                Is.EquivalentTo(new[] { "Фервидус", "Мерак", "Асклепиу", "Аэрумна", "Траскиас" }));
            var environment = entities.GetComponent<SpaceEnvironmentComponent>(map);
            var plasma = new SpaceRegion { Id = "test-plasma", Kind = SpaceRegionKind.Plasma,
                X = 5000, Radius = 400, Aspect = 0.5f };
            environment.Definition.Regions = new() { plasma };
            var system = entities.System<SpaceEnvironmentSystem>();
            system.Invalidate(map);
            Assert.That(system.BlocksFtl(map, new Vector2(5000, 0)), Is.True);
            Assert.That(system.BlocksFtl(map, new Vector2(5000, 250)), Is.False);
            Assert.That(system.BlocksFtl(map, new Vector2(5500, 0)), Is.False);
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }
    [Test]
    public async Task SurveyRequiresPowerAndLockAccessAndSharesItsCooldownAcrossShipConsoles()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var cfg = pair.Server.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(SpaceEnvironmentCVars.Campaign, "integration-survey");
            var maps = entities.System<SharedMapSystem>();
            var map = maps.CreateMap(out var mapId);
            var sector = new SectorDefinition { Id = "survey-test", Regions = new()
                { new SpaceRegion { Id = "signal", Kind = SpaceRegionKind.Dust, Radius = 500 } } };
            var sectors = entities.System<SectorSystem>();
            sectors.Store.GetOrCreate(sectors.Campaign, sector);
            sectors.Bind(map, sector);
            var grid = maps.CreateGridEntity(mapId);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var x = 0; x < 4; x++) maps.SetTile(grid, new Vector2i(x, 0), floor);
            var first = entities.SpawnEntity("ComputerShuttle", new EntityCoordinates(grid.Owner, new Vector2(0.5f, 0.5f)));
            var second = entities.SpawnEntity("ComputerShuttle", new EntityCoordinates(grid.Owner, new Vector2(1.5f, 0.5f)));
            var actor = entities.SpawnEntity("MobHuman", new EntityCoordinates(grid.Owner, new Vector2(0.5f, 0.5f)));
            entities.RemoveComponent<AccessReaderComponent>(first);
            entities.RemoveComponent<AccessReaderComponent>(second);
            entities.RemoveComponent<ShuttleConsoleLockComponent>(second);
            var scan = entities.System<SpaceSurveySystem>();
            entities.GetComponent<ApcPowerReceiverComponent>(first).Powered = false;
            Assert.That(scan.Scan(first, actor), Is.False);
            entities.GetComponent<ApcPowerReceiverComponent>(first).Powered = true;
            entities.EnsureComponent<ShipGridLockComponent>(grid.Owner);
            var locks = entities.System<ShuttleConsoleLockSystem>();
            locks.SetShuttleId(first, "survey-test");
            Assert.That(entities.GetComponent<ShipGridLockComponent>(grid.Owner).Locked, Is.True);
            Assert.That(scan.Scan(first, actor), Is.False, "The effective ship lock must deny scanning");
            locks.SetShuttleId(first, string.Empty);
            Assert.That(entities.GetComponent<ShipGridLockComponent>(grid.Owner).Locked, Is.False);
            entities.GetComponent<ApcPowerReceiverComponent>(second).Powered = true;
            Assert.That(scan.Scan(first, actor), Is.True);
            Assert.That(scan.Scan(second, actor), Is.False, "Another console must not bypass the ship cooldown");
            Assert.That(entities.GetComponent<SpaceEnvironmentComponent>(map).Surveyed, Is.EquivalentTo(new[] { "signal" }));
            Assert.That(sectors.Store.Surveys(sectors.Campaign, sector.Id), Is.EquivalentTo(new[] { "signal" }));
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EnvironmentalRadiationUsesNativeReceiverEventAndRespectsDisableSwitch()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var cfg = pair.Server.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(SpaceEnvironmentCVars.Campaign, "integration-radiation");
            var sectors = entities.System<SectorSystem>();
            var map = sectors.Create("radiation-test", 917, "DeepLagoonRadiationSea");
            var environment = entities.GetComponent<SpaceEnvironmentComponent>(map);
            environment.Definition.Regions = new() { new SpaceRegion { Id = "radiation", Kind = SpaceRegionKind.Radiation,
                Radius = 500, Intensity = 0.8f } };
            entities.System<SpaceEnvironmentSystem>().Invalidate(map);
            var human = entities.SpawnEntity("MobHuman", new EntityCoordinates(map, Vector2.Zero));
            entities.EnsureComponent<RadiationReceiverComponent>(human);
            var radiation = new GetAmbientRadiationEvent();
            entities.EventBus.RaiseLocalEvent(human, ref radiation);
            Assert.That(radiation.Radiation, Is.EqualTo(0.4f).Within(0.001f));
            cfg.SetCVar(SpaceEnvironmentCVars.Enabled, false);
            var disabled = new GetAmbientRadiationEvent();
            entities.EventBus.RaiseLocalEvent(human, ref disabled);
            Assert.That(disabled.Radiation, Is.Zero);
            cfg.SetCVar(SpaceEnvironmentCVars.Enabled, true);
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SectorCapacityAndUnloadPreserveHistoryAndNeverDeleteOccupiedGrids()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var cfg = pair.Server.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(SpaceEnvironmentCVars.Campaign, "integration-sector-capacity");
            cfg.SetCVar(SpaceEnvironmentCVars.MaxLoaded, 1);
            var sectors = entities.System<SectorSystem>();
            var map = sectors.Create("first", 17, "DeepLagoonQuietReach");
            Assert.That(sectors.Create("first", 99, "DeepLagoonFrontier"), Is.EqualTo(map));
            Assert.Throws<InvalidOperationException>(() => sectors.Create("second", 18, "DeepLagoonFrontier"));
            var maps = entities.System<SharedMapSystem>();
            var grid = maps.CreateGridEntity(entities.GetComponent<TransformComponent>(map).MapID);
            Assert.That(sectors.TryUnload(map), Is.False);
            entities.DeleteEntity(grid.Owner);
            Assert.That(sectors.TryUnload(map), Is.True);
            Assert.That(sectors.LoadedCount, Is.Zero);
            Assert.That(sectors.Store.Load(sectors.Campaign, "first")!.Seed, Is.EqualTo(17));
            var reopened = sectors.Create("first", 999, "DeepLagoonFrontier");
            Assert.That(entities.GetComponent<SpaceEnvironmentComponent>(reopened).Definition.Seed, Is.EqualTo(17));
            Assert.That(sectors.TryUnload(reopened), Is.True);
            cfg.SetCVar(SpaceEnvironmentCVars.MaxLoaded, 8);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SectorManifestAndSurveyReachTheClientWithTheSamePlanetAppearance()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        EntityUid map = default, user = default, watchedPile = default;
        Vector2 observerOrigin = default;
        EntityUid detachedLoot = default;
        NetEntity netMap = default;
        string[] names = null;
        float[] rotations = null;
        string regionId = null;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(SpaceEnvironmentCVars.Campaign, "integration-network");
            map = entities.System<SharedMapSystem>().CreateMap(out _);
            var star = entities.EnsureComponent<StarSystemMapComponent>(map);
            entities.System<StarMapSystem>().SetSystem((map, star), "SystemKyphrus");
            names = star.StarSystem!.Planets.Select(planet => planet.Name).ToArray();
            rotations = star.StarSystem.Planets.Select(planet => planet.Rotation).ToArray();
            regionId = entities.GetComponent<SpaceEnvironmentComponent>(map).Definition.Regions[0].Id;
            netMap = entities.GetNetEntity(map);
            var field = entities.GetComponent<SpaceEnvironmentComponent>(map).Definition.Regions.First(r => r.Kind == SpaceRegionKind.Debris);
            observerOrigin = new Vector2(field.X, field.Y);
            user = entities.SpawnEntity("MobHuman", new EntityCoordinates(map, observerOrigin));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), user);
        });
        await pair.RunTicksSync(90);
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var clientMap = entities.GetEntity(netMap);
            var star = entities.GetComponent<StarSystemMapComponent>(clientMap);
            var navigation = entities.System<Content.Client._DeepLagoon.SpaceEnvironment.SpaceEnvironmentOverlaySystem>();
            foreach (var planet in star.StarSystem!.Planets)
            {
                var portrait = navigation.PlanetShader(clientMap, star.StarSystem, planet);
                Assert.That(portrait, Is.Not.Null, planet.Name);
                Assert.That(navigation.PlanetShader(clientMap, star.StarSystem, planet), Is.SameAs(portrait));
            }
            Assert.That(star.StarSystem!.Planets.Select(planet => planet.Name), Is.EqualTo(names));
            Assert.That(star.StarSystem.Planets.Select(planet => planet.Rotation), Is.EqualTo(rotations));
            Assert.That(entities.GetComponent<SpaceEnvironmentComponent>(clientMap).Definition.Regions, Is.Not.Empty);
            pair.Client.ResolveDependency<IConfigurationManager>().SetCVar(
                Content.Client._DeepLagoon.SpaceEnvironment.SpaceEnvironmentClientCVars.Visuals, false);
            Assert.That(star.StarSystem.Planets, Has.Count.EqualTo(5), "Cosmetic quality must not remove navigation data");
        });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var debris = entities.System<OrbitalDebrisSystem>();
            Assert.That(debris.ActiveCount, Is.GreaterThan(0), "An active observer must materialize local debris");
            watchedPile = debris.SpawnPile(map, observerOrigin + new Vector2(20, 0), observerOrigin + new Vector2(1000, 0), 51)!.Value;
            debris.ClearUnclaimed(map);
            Assert.That(entities.EntityExists(watchedPile), Is.True, "A visible pile must survive explicit ambient cleanup");
            var children = entities.GetComponent<TransformComponent>(watchedPile).ChildEnumerator;
            while (children.MoveNext(out var child))
                if (entities.HasComponent<Content.Shared.Item.ItemComponent>(child)
                    && !entities.GetComponent<TransformComponent>(child).Anchored) { detachedLoot = child; break; }
            Assert.That(detachedLoot, Is.Not.EqualTo(EntityUid.Invalid), "The field pile should contain a portable find");
            var transform = entities.System<SharedTransformSystem>();
            transform.SetCoordinates(detachedLoot, new EntityCoordinates(map, observerOrigin + new Vector2(-10, 0)));
            debris.Update(0);
            Assert.That(entities.EntityExists(detachedLoot), Is.True, "Detached loot stays while the player can see it");
            Assert.That(entities.System<SectorSystem>().TryUnload(map), Is.False, "An occupied map cannot be unloaded");
            Assert.That(entities.EntityExists(watchedPile), Is.True);
            Assert.That(entities.System<SectorSystem>().Survey(map, regionId), Is.True);
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.EntityExists(detachedLoot), Is.True, "Visible detached loot must survive deletion processing");
            entities.System<SharedTransformSystem>().SetCoordinates(detachedLoot,
                new EntityCoordinates(map, observerOrigin + new Vector2(5000, 0)));
            entities.System<OrbitalDebrisSystem>().Update(0);
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.EntityExists(detachedLoot), Is.False,
            "Detached loot is culled after leaving the player's screen envelope"));
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            Assert.That(entities.GetComponent<SpaceEnvironmentComponent>(entities.GetEntity(netMap)).Surveyed, Does.Contain(regionId));
        });
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null);
            pair.Server.EntMan.DeleteEntity(user);
            Assert.That(pair.Server.EntMan.System<SectorSystem>().TryUnload(map), Is.True);
        });
        await pair.RunTicksSync(5);
        await pair.CleanReturnAsync();
    }

}

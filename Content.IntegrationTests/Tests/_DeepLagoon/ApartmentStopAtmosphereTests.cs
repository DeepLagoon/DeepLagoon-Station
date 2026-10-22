using System.Linq;
using System.Reflection;
using Content.Server._DeepLagoon.Apartments;
using Content.Server._DeepLagoon.Mapping;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Atmos;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentStopAtmosphereTests
{
    [Test]
    public async Task StopFloorsKeepSafeAirOnSpaceMapAndLegacyStopsAreRepaired()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        EntityUid grid = default, map = default, body = default;
        void AssertSafeAir()
        {
            var entities = pair.Server.EntMan;
            var atmosphere = entities.System<AtmosphereSystem>();
            var tiles = entities.System<SharedMapSystem>().GetAllTiles(grid, entities.GetComponent<MapGridComponent>(grid)).ToArray();
            Assert.That(tiles, Is.Not.Empty);
            Assert.That(entities.GetComponent<GridAtmosphereComponent>(grid).Simulated, Is.False);
            foreach (var tile in tiles)
            {
                var air = atmosphere.GetTileMixture(grid, map, tile.GridIndices);
                Assert.That(air, Is.Not.Null);
                Assert.That(air!.Immutable, Is.True);
                Assert.That(air.Pressure, Is.InRange(100, 103));
                Assert.That(air.Temperature, Is.EqualTo(Atmospherics.T20C).Within(.01));
                Assert.That(air.GetMoles(Gas.Oxygen), Is.GreaterThan(0));
                Assert.That(air.GetMoles(Gas.Plasma), Is.Zero);
                air.AdjustMoles(Gas.Plasma, 100);
                air.Temperature = 1000;
                Assert.That(air.GetMoles(Gas.Plasma), Is.Zero);
                Assert.That(air.Temperature, Is.EqualTo(Atmospherics.T20C).Within(.01));
            }
            var breathed = atmosphere.GetContainingMixture(body);
            Assert.That(breathed!.Pressure, Is.InRange(100, 103));
            var bounds = entities.GetComponent<MapGridComponent>(grid).LocalAABB;
            var outside = new Vector2i((int) bounds.Right + 5, (int) bounds.Top + 5);
            Assert.That(atmosphere.GetTileMixture(grid, map, outside)?.Pressure ?? 0, Is.LessThan(.01));
        }
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            map = entities.System<SharedMapSystem>().CreateMap(out var mapId);
            var loader = entities.System<MapLoaderSystem>();
            Assert.That(loader.TryLoadGridTracked(mapId, new ResPath("/Maps/_DeepLagoon/Apartments/residential_stop.yml"), out var loaded), Is.True);
            grid = loaded!.Value.Owner;
            Assert.That(entities.GetComponent<GridEnvironmentComponent>(grid).SafeAtmosphere, Is.True);
            var marker = entities.EntityQuery<ApartmentTransitStopComponent, TransformComponent>().First(item => item.Item2.GridUid == grid).Item2;
            body = entities.SpawnEntity("MobHuman", marker.Coordinates);
            AssertSafeAir();
        });
        await pair.RunTicksSync(180);
        await pair.Server.WaitAssertion(() =>
        {
            AssertSafeAir();
            var entities = pair.Server.EntMan;
            entities.RemoveComponent<GridEnvironmentComponent>(grid);
            entities.RemoveComponent<GridAtmosphereComponent>(grid);
            typeof(ApartmentStopLoaderSystem).GetMethod("EnsureSafeAtmosphere", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(entities.System<ApartmentStopLoaderSystem>(), new object[] { grid });
            Assert.That(entities.GetComponent<GridEnvironmentComponent>(grid).SafeAtmosphere, Is.True);
            AssertSafeAir();
        });
        await pair.RunTicksSync(60);
        await pair.Server.WaitAssertion(AssertSafeAir);
        await pair.CleanReturnAsync();
    }
}

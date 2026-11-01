using System.Numerics;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Shared._DeepLagoon.Salvage;
using Content.Shared.Atmos;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class ExpeditionAtmosphereTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ArrivalAndRepeatedTransfersKeepCabinAir(bool emptyCabin)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        EntityUid space = default, planet = default, shuttle = default, passenger = default;
        GasMixture cabin = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var xforms = entities.System<SharedTransformSystem>();
            space = maps.CreateMap(out var spaceId);
            entities.System<AtmosphereSystem>().SetMapAtmosphere(space, true, GasMixture.SpaceGas);
            planet = maps.CreateMap(out _);
            entities.AddComponent<ExpeditionGroundComponent>(planet);
            var planetaryAir = new GasMixture(Atmospherics.CellVolume) { Temperature = 320f };
            planetaryAir.SetMoles(Gas.Plasma, 200f);
            entities.System<AtmosphereSystem>().SetMapAtmosphere(planet, false, planetaryAir);
            var grid = maps.CreateGridEntity(spaceId);
            shuttle = grid.Owner;
            entities.EnsureComponent<ShuttleComponent>(shuttle);
            var plating = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var x = -1; x <= 1; x++)
            for (var y = -1; y <= 1; y++)
            {
                var index = new Vector2i(x, y);
                maps.SetTile(grid, index, plating);
                if (index == Vector2i.Zero)
                    continue;
                var wall = entities.SpawnEntity("WallSolid", new EntityCoordinates(shuttle, new Vector2(x + 0.5f, y + 0.5f)));
                xforms.AnchorEntity(wall);
            }
            entities.EnsureComponent<GridAtmosphereComponent>(shuttle);
            passenger = entities.SpawnEntity(null, new EntityCoordinates(shuttle, new Vector2(0.5f, 0.5f)));
        });
        await pair.RunTicksSync(90);
        await pair.Server.WaitAssertion(() =>
        {
            cabin = pair.Server.EntMan.System<AtmosphereSystem>().GetTileMixture(shuttle, space, Vector2i.Zero);
            Assert.That(cabin, Is.Not.Null);
            Assert.That(cabin.Immutable, Is.False);
            cabin.Clear();
            cabin.Temperature = Atmospherics.T20C;
            if (!emptyCabin)
            {
                cabin.SetMoles(Gas.Oxygen, 20f);
                cabin.SetMoles(Gas.Nitrogen, 80f);
            }
        });
        foreach (var destination in new[] { planet, space, planet })
        {
            await pair.Server.WaitAssertion(() => pair.Server.EntMan.System<SharedTransformSystem>()
                .SetCoordinates(shuttle, new EntityCoordinates(destination, Vector2.Zero)));
            await pair.RunTicksSync(120);
            await pair.Server.WaitAssertion(() =>
            {
                var entities = pair.Server.EntMan;
                var atmos = entities.System<AtmosphereSystem>();
                var air = atmos.GetTileMixture(shuttle, destination, Vector2i.Zero);
                Assert.That(air, Is.SameAs(cabin), "Changing maps must preserve the cabin's mixture.");
                Assert.That(air.GetMoles(Gas.Plasma), Is.Zero);
                Assert.That(air.GetMoles(Gas.Oxygen), Is.EqualTo(emptyCabin ? 0f : 20f).Within(0.01f));
                Assert.That(air.GetMoles(Gas.Nitrogen), Is.EqualTo(emptyCabin ? 0f : 80f).Within(0.01f));
                Assert.That(atmos.GetContainingMixture(passenger), Is.SameAs(cabin));
                Assert.That(atmos.GetTileMixture(shuttle, destination, new Vector2i(10, 0)).TotalMoles, Is.Zero);
                var mixtures = atmos.GetTileMixtures(shuttle, destination, [Vector2i.Zero, new Vector2i(10, 0)]);
                Assert.That(mixtures[0], Is.SameAs(cabin));
                Assert.That(mixtures[1].TotalMoles, Is.Zero);
                Assert.That(atmos.IsTileSpace(shuttle, destination, new Vector2i(10, 0)), Is.True);
                Assert.That(atmos.GetTileMixture(null, planet, Vector2i.Zero).GetMoles(Gas.Plasma), Is.EqualTo(200f));
            });
        }
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var changedAir = new GasMixture(Atmospherics.CellVolume);
            changedAir.SetMoles(Gas.CarbonDioxide, 300f);
            entities.System<AtmosphereSystem>().SetMapGasMixture(planet, changedAir);
        });
        await pair.RunTicksSync(120);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var atmos = entities.System<AtmosphereSystem>();
            Assert.That(atmos.GetTileMixture(shuttle, planet, Vector2i.Zero), Is.SameAs(cabin));
            Assert.That(cabin.GetMoles(Gas.CarbonDioxide), Is.Zero);
            Assert.That(atmos.GetTileMixture(shuttle, planet, new Vector2i(10, 0)).TotalMoles, Is.Zero);
            entities.DeleteEntity(planet);
            entities.DeleteEntity(space);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(true, true, false)]
    [TestCase(false, true, true)]
    [TestCase(true, false, true)]
    public async Task OnlyExpeditionShuttlesExcludePlanetaryGas(bool isShuttle, bool isExpedition, bool getsPlanetGas)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        EntityUid map = default, gridUid = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            map = maps.CreateMap(out var mapId);
            if (isExpedition)
                entities.AddComponent<ExpeditionGroundComponent>(map);
            var mixture = new GasMixture(Atmospherics.CellVolume);
            mixture.SetMoles(Gas.Plasma, 200f);
            entities.System<AtmosphereSystem>().SetMapAtmosphere(map, false, mixture);
            var grid = maps.CreateGridEntity(mapId);
            gridUid = grid.Owner;
            maps.SetTile(grid, Vector2i.Zero, new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Lattice"].TileId));
            if (isShuttle)
                entities.EnsureComponent<ShuttleComponent>(grid);
            else
                entities.RemoveComponent<ShuttleComponent>(grid);
            entities.EnsureComponent<GridAtmosphereComponent>(grid);
            var air = entities.System<AtmosphereSystem>().GetTileMixture(gridUid, map, new Vector2i(10, 0));
            Assert.That(air.GetMoles(Gas.Plasma), Is.EqualTo(getsPlanetGas ? 200f : 0f), "Fallback before atmos processing.");
        });
        await pair.RunTicksSync(120);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var atmos = entities.System<AtmosphereSystem>();
            Assert.That(atmos.GetTileMixture(gridUid, map, Vector2i.Zero).GetMoles(Gas.Plasma), Is.EqualTo(getsPlanetGas ? 200f : 0f));
            Assert.That(atmos.IsTileSpace(gridUid, map, Vector2i.Zero), Is.EqualTo(!getsPlanetGas));
            Assert.That(atmos.GetTileMixture(null, map, Vector2i.Zero).GetMoles(Gas.Plasma), Is.EqualTo(200f));
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }
}

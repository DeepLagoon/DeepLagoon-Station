using System.Collections.Generic;
using System.Numerics;
using Content.Server.Explosion.EntitySystems;
using Content.Server.ImmovableRod;
using Content.Server.Singularity.EntitySystems;
using Content.Shared._DeepLagoon.Salvage;
using Content.Shared.Anomaly.Components;
using Content.Shared.Anomaly.Effects.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Explosion;
using Content.Shared.FixedPoint;
using Content.Shared.Maps;
using Content.Shared.RCD;
using Content.Shared.RCD.Components;
using Content.Shared.RCD.Systems;
using Content.Shared.Singularity.Components;
using Content.Shared.Tools.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class ExpeditionGroundTests
{
    [TestCase("FloorSteel", "Plating")]
    [TestCase("Plating", "Plating")]
    [TestCase("PlatingDamaged", "PlatingDamaged")]
    [TestCase("FloorAsteroidSand", "FloorAsteroidSand")]
    [TestCase("FloorAsteroidSandPlanet", "FloorAsteroidSandPlanet")]
    [TestCase("FloorPlanetGrass", "FloorPlanetGrass")]
    public async Task RepeatedXenoAcidCannotDestroyGround(string initial, string expected)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var definitions = pair.Server.ResolveDependency<ITileDefinitionManager>();
            var map = maps.CreateMap(out var mapId);
            var ground = entities.EnsureComponent<MapGridComponent>(map);
            entities.AddComponent<ExpeditionGroundComponent>(map);
            maps.SetTile(map, ground, Vector2i.Zero, new Tile(definitions[initial].TileId));
            var acid = pair.Server.ProtoMan.Index<ReagentPrototype>("FluorosulfuricAcid");
            Assert.That(acid.TileReactions, Is.Not.Empty);
            for (var i = 0; i < 10; i++)
                acid.ReactionTile(maps.GetTileRef(map, ground, Vector2i.Zero), FixedPoint2.New(10), entities, null);
            Assert.That(maps.GetTileRef(map, ground, Vector2i.Zero).Tile.TypeId, Is.EqualTo(definitions[expected].TileId));

            // A visiting shuttle on the same map must keep ordinary hull destruction.
            var shuttle = maps.CreateGridEntity(mapId);
            maps.SetTile(shuttle, Vector2i.Zero, new Tile(definitions["FloorSteel"].TileId));
            for (var i = 0; i < 3; i++)
                acid.ReactionTile(maps.GetTileRef(shuttle.Owner, shuttle.Comp, Vector2i.Zero), FixedPoint2.New(10), entities, null);
            Assert.That(maps.GetTileRef(shuttle.Owner, shuttle.Comp, Vector2i.Zero).Tile.IsEmpty, Is.True);
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ToolsRemoveCoveringButNotSubfloorAndRcdCannotOpenGroundToSpace()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var definitions = pair.Server.ResolveDependency<ITileDefinitionManager>();
            var map = maps.CreateMap(out var mapId);
            var ground = entities.EnsureComponent<MapGridComponent>(map);
            entities.AddComponent<ExpeditionGroundComponent>(map);
            maps.SetTile(map, ground, Vector2i.Zero, new Tile(definitions["FloorSteel"].TileId));
            var tiles = entities.System<TileSystem>();
            Assert.That(tiles.PryTile(maps.GetTileRef(map, ground, Vector2i.Zero)), Is.True);
            Assert.That(maps.GetTileRef(map, ground, Vector2i.Zero).Tile.TypeId, Is.EqualTo(definitions["Plating"].TileId));
            Assert.That(entities.System<SharedToolSystem>().TryDeconstructWithToolQualities(
                maps.GetTileRef(map, ground, Vector2i.Zero), ["Axing"]), Is.False);
            Assert.That(tiles.DeconstructTile(maps.GetTileRef(map, ground, Vector2i.Zero)), Is.False);

            var rcd = entities.SpawnEntity("RCD", new EntityCoordinates(map, new Vector2(0.5f, 0.5f)));
            var comp = entities.GetComponent<RCDComponent>(rcd);
#pragma warning disable RA0002 // Fixture sets the selected RCD recipe.
            comp.ProtoId = "Deconstruct";
            comp.CachedPrototype = pair.Server.ProtoMan.Index<RCDPrototype>(comp.ProtoId);
#pragma warning restore RA0002
            var system = entities.System<RCDSystem>();
            Assert.That(system.TryGetMapGridData(new EntityCoordinates(map, new Vector2(0.5f, 0.5f)), out var data), Is.True);
            Assert.That(system.IsRCDOperationStillValid(rcd, comp, data.Value, null, rcd, popMsgs: false), Is.False);
            var shuttle = maps.CreateGridEntity(mapId);
            maps.SetTile(shuttle, Vector2i.Zero, new Tile(definitions["Plating"].TileId));
            Assert.That(system.TryGetMapGridData(new EntityCoordinates(shuttle, new Vector2(0.5f, 0.5f)), out data), Is.True);
            Assert.That(system.IsRCDOperationStillValid(rcd, comp, data.Value, null, rcd, popMsgs: false), Is.True);
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("FloorSteel", "Plating")]
    [TestCase("Plating", "Plating")]
    [TestCase("FloorAsteroidSandPlanet", "FloorAsteroidSandPlanet")]
    public async Task ExplosionCannotBreakThroughGround(string initial, string expected)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var definitions = pair.Server.ResolveDependency<ITileDefinitionManager>();
            var map = maps.CreateMap(out var mapId);
            var ground = entities.EnsureComponent<MapGridComponent>(map);
            entities.AddComponent<ExpeditionGroundComponent>(map);
            var explosion = new ExplosionPrototype { _tileBreakChance = [1f], _tileBreakIntensity = [0f], TileBreakRerollReduction = 0f };
            foreach (var grid in new[] { new Entity<MapGridComponent>(map, ground), maps.CreateGridEntity(mapId) })
            {
                maps.SetTile(grid, Vector2i.Zero, new Tile(definitions[initial].TileId));
                for (var i = 0; i < 3; i++)
                {
                    var damaged = new List<(Vector2i GridIndices, Tile Tile)>();
                    entities.System<ExplosionSystem>().DamageFloorTile(maps.GetTileRef(grid.Owner, grid.Comp, Vector2i.Zero),
                        1000f, 100, true, damaged, explosion);
                    maps.SetTiles(grid, damaged);
                }
                if (grid.Owner == map)
                    Assert.That(maps.GetTileRef(grid.Owner, grid.Comp, Vector2i.Zero).Tile.TypeId, Is.EqualTo(definitions[expected].TileId));
                else if (initial == "FloorSteel" || initial == "Plating")
                    Assert.That(maps.GetTileRef(grid.Owner, grid.Comp, Vector2i.Zero).Tile.IsEmpty, Is.True);
            }
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("Rod")]
    [TestCase("Singularity")]
    [TestCase("GravityAnomaly")]
    public async Task DirectTileDestroyersPreserveGround(string destroyer)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var definitions = pair.Server.ResolveDependency<ITileDefinitionManager>();
            var map = maps.CreateMap(out var mapId);
            var ground = entities.EnsureComponent<MapGridComponent>(map);
            entities.AddComponent<ExpeditionGroundComponent>(map);
            var shuttle = maps.CreateGridEntity(mapId);
            entities.System<SharedTransformSystem>().SetWorldPosition(shuttle, new Vector2(20, 0));
            foreach (var grid in new[] { new Entity<MapGridComponent>(map, ground), shuttle })
            {
                maps.SetTile(grid, Vector2i.Zero, new Tile(definitions["Plating"].TileId));
                var source = entities.SpawnEntity(null, new EntityCoordinates(grid, new Vector2(0.5f, 0.5f)));
                switch (destroyer)
                {
                    case "Rod":
                        entities.AddComponent<ImmovableRodComponent>(source);
                        entities.System<ImmovableRodSystem>().Update(0f);
                        break;
                    case "Singularity":
                        var horizon = entities.AddComponent<EventHorizonComponent>(source);
                        entities.System<EventHorizonSystem>().ConsumeTile(source, maps.GetTileRef(grid.Owner, grid.Comp, Vector2i.Zero), horizon);
                        break;
                    case "GravityAnomaly":
                        entities.AddComponent<GravityAnomalyComponent>(source);
                        var ev = new AnomalySupercriticalEvent(source, 1f);
                        entities.EventBus.RaiseLocalEvent(source, ref ev);
                        break;
                }
                Assert.That(maps.GetTileRef(grid.Owner, grid.Comp, Vector2i.Zero).Tile.IsEmpty,
                    Is.EqualTo(grid.Owner != map), destroyer);
                entities.DeleteEntity(source);
            }
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }
}

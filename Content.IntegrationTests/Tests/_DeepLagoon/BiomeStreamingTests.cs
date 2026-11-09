using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.NPC.Pathfinding;
using Content.Server.Parallax;
using Content.Server.Salvage.Expeditions;
using Content.Shared.Parallax.Biomes;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BiomeStreamingTests
{
    private static readonly ProtoId<BiomeTemplatePrototype> EmptyBiome = "DLTestStreamingGround";
    private static readonly ProtoId<BiomeTemplatePrototype> RockBiome = "DLTestStreamingRocks";
    private const string Marker = "DLTestStreamingMarker";

    [TestPrototypes]
    private const string Prototypes = """
        - type: biomeTemplate
          id: DLTestStreamingGround
          layers:
          - !type:BiomeTileLayer
            threshold: -1
            tile: FloorAsteroidSandPlanet

        - type: biomeTemplate
          id: DLTestStreamingRocks
          layers:
          - !type:BiomeTileLayer
            threshold: -1
            tile: FloorAsteroidSandPlanet
          - !type:BiomeEntityLayer
            threshold: -1
            allowedTiles: [FloorAsteroidSandPlanet]
            entities: [MonoPlanetmapOreBase]

        - type: biomeMarkerLayer
          id: DLTestStreamingMarker
          size: 128
          radius: 4
          maxCount: 1
          prototype: FloraRockSolid
        """;

    [TestCase("DLTestStreamingGround")]
    [TestCase("Caves")]
    public async Task MovingAdminGhostStreamsWithBoundedWorkAndDropsItsTrail(string template)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var entities = pair.Server.EntMan;
        EntityUid map = default, ghost = default;
        MapGridComponent grid = default;
        BiomeComponent biome = default;
        GridPathfindingComponent navigation = default;
        var previous = new HashSet<Vector2i>();
        var previousNavigation = new HashSet<Vector2i>();
        var peakLoaded = 0;
        var totalLoaded = 0;
        var peakNavigation = 0;

        await pair.Server.WaitAssertion(() =>
        {
            (map, grid, biome, ghost) = SpawnPlanet(entities, pair.Server.ProtoMan.Index(new ProtoId<BiomeTemplatePrototype>(template)));
            navigation = entities.GetComponent<GridPathfindingComponent>(map);
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(CVars.NetMaxUpdateRange, 25f);
            config.SetCVar(CVars.NetTickrate, 30);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Player!, ghost);
        });

        // Move as far as a maximum-speed observer every tick, then jump to a disjoint window.
        for (var tick = 0; tick < 140; tick++)
        {
            var position = new Vector2(tick * 2.56f + (tick >= 60 ? 256f : 0f), tick * 1.92f);
            await pair.Server.WaitPost(() => entities.System<SharedTransformSystem>()
                .SetCoordinates(ghost, new EntityCoordinates(map, position)));
            await pair.RunTicksSync(1);
            await pair.Server.WaitAssertion(() =>
            {
                var added = biome.LoadedChunks.Count(chunk => !previous.Contains(chunk));
                Assert.That(added, Is.LessThanOrEqualTo(4));
                totalLoaded += added;
                peakLoaded = Math.Max(peakLoaded, biome.LoadedChunks.Count);
                previous.Clear();
                previous.UnionWith(biome.LoadedChunks);
                Assert.That(navigation.Chunks.Keys.Count(chunk => !previousNavigation.Contains(chunk)), Is.LessThanOrEqualTo(8));
                peakNavigation = Math.Max(peakNavigation, navigation.Chunks.Count);
                previousNavigation.Clear();
                previousNavigation.UnionWith(navigation.Chunks.Keys);
            });
        }

        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(totalLoaded, Is.GreaterThan(100));
            Assert.That(peakNavigation, Is.GreaterThan(0));
            Assert.That(peakLoaded, Is.LessThanOrEqualTo(170), $"Streamed {totalLoaded} chunks, loaded {biome.LoadedChunks.Count} now.");
            Assert.That(entities.System<SharedMapSystem>().GetTileRef(map, grid, Vector2i.Zero).Tile.IsEmpty, Is.True);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Player!, null);
        });

        await pair.RunTicksSync(150);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(biome.LoadedChunks, Is.Empty);
            var maps = entities.System<SharedMapSystem>();
            var remaining = maps.GetAllTiles(map, grid).Take(12).Select(tile =>
            {
                var anchored = maps.GetAnchoredEntities(map, grid, tile.GridIndices);
                return $"{tile.GridIndices} ({(anchored.MoveNext(out var ent) ? entities.GetComponent<MetaDataComponent>(ent.Value).EntityPrototype?.ID : "no anchor")})";
            }).ToArray();
            Assert.That(remaining, Is.Empty, $"Terrain left behind: {string.Join(", ", remaining)}");
            Assert.That(navigation.Chunks, Is.Empty);
            entities.DeleteEntity(map);
        });
        TestContext.Out.WriteLine($"Streamed {totalLoaded} chunks over 140 moving ticks, peak resident {peakLoaded}.");
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UnloadingPreservesEditsAndRocksKeepTheirRoll()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var entities = pair.Server.EntMan;
        var maps = entities.System<SharedMapSystem>();
        EntityUid map = default, ghost = default, originalRock = default, renamedRock = default;
        MapGridComponent grid = default;
        BiomeComponent biome = default;
        var untouched = new Vector2i(1, 1);
        var mined = new Vector2i(2, 1);
        var edited = new Vector2i(3, 1);
        var authored = new Vector2i(4, 1);
        var renamed = new Vector2i(5, 1);
        string rock = default;
        Tile editedTile = default, authoredTile = default;

        await pair.Server.WaitAssertion(() =>
        {
            (map, grid, biome, ghost) = SpawnPlanet(entities, pair.Server.ProtoMan.Index(RockBiome));
            var tiles = pair.Server.ResolveDependency<ITileDefinitionManager>();
            editedTile = new Tile(tiles["FloorSteel"].TileId);
            Assert.That(entities.System<BiomeSystem>().TryGetBiomeTile(map, grid, authored, out var generated), Is.True);
            authoredTile = generated!.Value;
            maps.SetTile(map, grid, authored, authoredTile);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Player!, ghost);
        });

        await pair.RunTicksSync(4);
        await pair.Server.WaitAssertion(() =>
        {
            var generated = biome.LoadedEntities[Vector2i.Zero];
            originalRock = generated.Single(pair => pair.Value == untouched).Key;
            rock = entities.GetComponent<MetaDataComponent>(originalRock).EntityPrototype!.ID;
            Assert.That(rock, Does.StartWith("WallRock"));
            entities.DeleteEntity(generated.Single(pair => pair.Value == mined).Key);
            renamedRock = generated.Single(pair => pair.Value == renamed).Key;
            entities.System<MetaDataSystem>().SetEntityName(renamedRock, "edited rock");
            maps.SetTile(map, grid, edited, editedTile);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Player!, null);
        });

        await pair.RunTicksSync(40);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(biome.LoadedChunks, Is.Empty);
            Assert.That(entities.EntityExists(originalRock), Is.False);
            Assert.That(entities.GetComponent<MetaDataComponent>(renamedRock).EntityName, Is.EqualTo("edited rock"));
            Assert.That(maps.GetTileRef(map, grid, untouched).Tile.IsEmpty, Is.True);
            Assert.That(maps.GetTileRef(map, grid, edited).Tile, Is.EqualTo(editedTile));
            Assert.That(maps.GetTileRef(map, grid, authored).Tile, Is.EqualTo(authoredTile));
            ghost = entities.SpawnEntity("AdminObserver", new EntityCoordinates(map, new Vector2(0.5f, 0.5f)));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Player!, ghost);
        });

        await pair.RunTicksSync(18);
        await pair.Server.WaitAssertion(() =>
        {
            var generated = biome.LoadedEntities[Vector2i.Zero];
            Assert.That(generated.Values, Does.Not.Contain(mined));
            var regenerated = generated.Single(pair => pair.Value == untouched).Key;
            Assert.That(entities.GetComponent<MetaDataComponent>(regenerated).EntityPrototype!.ID, Is.EqualTo(rock));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Player!, null);
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MarkerPreloadFinishesInSlicesWithoutAViewer()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var entities = pair.Server.EntMan;
        EntityUid map = default;
        BiomeComponent biome = default;

        await pair.Server.WaitAssertion(() =>
        {
            (map, _, biome, _) = SpawnPlanet(entities, pair.Server.ProtoMan.Index(EmptyBiome));
            var biomes = entities.System<BiomeSystem>();
            biomes.AddMarkerLayer(map, biome, Marker);
            biomes.Preload(map, biome, new Box2(40, 40, 41, 41));
        });

        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() => Assert.That(biome.LoadedMarkers, Is.Empty));
        await pair.RunTicksSync(100);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(biome.LoadedMarkers[Marker], Does.Contain(Vector2i.Zero));
            Assert.That(biome.PendingMarkers.Values.SelectMany(layers => layers[Marker]).Count(), Is.EqualTo(1));
            Assert.That(biome.LoadedChunks, Is.Empty);
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    private static (EntityUid, MapGridComponent, BiomeComponent, EntityUid) SpawnPlanet(IEntityManager entities,
        BiomeTemplatePrototype template)
    {
        var maps = entities.System<SharedMapSystem>();
        var map = maps.CreateMap(out var mapId, runMapInit: false);
        var grid = entities.EnsureComponent<MapGridComponent>(map);
        var biome = entities.AddComponent<BiomeComponent>(map);
        var expedition = entities.AddComponent<SalvageExpeditionComponent>(map);
        expedition.EndTime = TimeSpan.FromDays(1);
        var biomes = entities.System<BiomeSystem>();
        biomes.SetTemplate(map, biome, template);
        biomes.SetSeed(map, biome, 1234);
        maps.InitializeMap(mapId);
        var ghost = entities.SpawnEntity("AdminObserver", new EntityCoordinates(map, new Vector2(0.5f, 0.5f)));
        return (map, grid, biome, ghost);
    }
}

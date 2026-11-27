using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.IntegrationTests.Pair;
using Content.Server.NPC.Pathfinding;
using Content.Server.Parallax;
using Content.Server.Salvage.Expeditions;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Parallax.Biomes.Markers;
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

    // Cancellation fixture injects the loader's reservation before stepping its private iterator.
    // Keep that test-only mutation outside the production component's restricted write API.
    private static HashSet<Vector2i> LoadedChunksForFixture(BiomeComponent biome) =>
        (HashSet<Vector2i>) typeof(BiomeComponent).GetField(nameof(BiomeComponent.LoadedChunks))!.GetValue(biome)!;

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
        var finalPosition = Vector2.Zero;

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
            finalPosition = position;
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

        // The observer can outrun a time-budgeted cave generator. Once it stops,
        // its actual current window must catch up; total throughput while flying
        // is not a correctness target and must not force synchronous bulk spawns.
        var finalChunk = SharedMapSystem.GetChunkIndices(finalPosition.Floored(), 8) * 8;
        await WaitForStreaming(pair, () => biome.LoadedChunks.Any(chunk => chunk == finalChunk) &&
            !entities.System<SharedMapSystem>().GetTileRef(map, grid, finalPosition.Floored()).Tile.IsEmpty,
            "The stopped observer's current terrain must load, even after jumping past the old backlog.");
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(totalLoaded, Is.GreaterThan(0));
            Assert.That(peakNavigation, Is.GreaterThan(0));
            Assert.That(peakLoaded, Is.LessThanOrEqualTo(170), $"Streamed {totalLoaded} chunks, loaded {biome.LoadedChunks.Count} now.");
            Assert.That(entities.System<SharedMapSystem>().GetTileRef(map, grid, Vector2i.Zero).Tile.IsEmpty, Is.True);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Player!, null);
        });

        await WaitForStreaming(pair, () => biome.LoadedChunks.Count == 0 && navigation.Chunks.Count == 0,
            "All observer terrain must unload after detaching, including partially processed chunks.");
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
    public async Task RockChunkCreationCanYieldBetweenIndividualEntities()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var entities = pair.Server.EntMan;
        await pair.Server.WaitAssertion(() =>
        {
            var (map, grid, biome, _) = SpawnPlanet(entities, pair.Server.ProtoMan.Index(RockBiome));
            biome.Enabled = false; // Drive just the cooperative operation, without observer scheduling.
            var biomes = entities.System<BiomeSystem>();
            var maps = entities.System<SharedMapSystem>();
            var definitions = pair.Server.ResolveDependency<ITileDefinitionManager>();
            var tile = new Tile(definitions["FloorAsteroidSandPlanet"].TileId);
            for (var x = 0; x < 8; x++) for (var y = 0; y < 8; y++) maps.SetTile(map, grid, new Vector2i(x, y), tile);
            var method = typeof(BiomeSystem).GetMethod("LoadEntities", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var steps = (IEnumerable<bool>)method.Invoke(biomes, [biome, map, grid, Vector2i.Zero, biome.Seed])!;
            using var enumerator = steps.GetEnumerator();
            var previous = 0;
            var yielded = 0;
            while (enumerator.MoveNext())
            {
                var count = biome.LoadedEntities.GetValueOrDefault(Vector2i.Zero)?.Count ?? 0;
                Assert.That(count - previous, Is.InRange(0, 1), "A scheduler step must never spawn an entire rock chunk.");
                previous = count;
                yielded++;
            }
            Assert.That(yielded, Is.EqualTo(64));
            Assert.That(biome.LoadedEntities[Vector2i.Zero], Has.Count.EqualTo(64));
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task InterruptedChunksResumeWithoutRerollsDuplicatesOrFalseTerrainEdits()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var entities = pair.Server.EntMan;
        await pair.Server.WaitAssertion(() =>
        {
            var (map, grid, biome, _) = SpawnPlanet(entities, pair.Server.ProtoMan.Index(RockBiome));
            var biomes = entities.System<BiomeSystem>();
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(BiomeSystem);
            var request = Activator.CreateInstance(type.GetNestedType("ChunkRequest", BindingFlags.NonPublic)!, map, biome, grid, Vector2i.Zero)!;
            var workType = type.GetNestedType("ChunkWork", BindingFlags.NonPublic)!;
            IEnumerator<bool> Load() => ((IEnumerable<bool>)type.GetMethod("LoadChunkSteps", hidden)!.Invoke(biomes, [request, false])!).GetEnumerator();
            IEnumerator<bool> Unload() => ((IEnumerable<bool>)type.GetMethod("UnloadChunkSteps", hidden)!.Invoke(biomes, [request])!).GetEnumerator();
            void Interrupt(string field, IEnumerator<bool> steps)
            {
                type.GetField(field, hidden)!.SetValue(biomes, Activator.CreateInstance(workType, request, steps));
                var shutdown = new ComponentShutdown();
                entities.EventBus.RaiseComponentEvent(map, biome, ref shutdown);
                Assert.That(type.GetField(field, hidden)!.GetValue(biomes), Is.Null);
                Assert.That(biome.LoadedChunks, Does.Not.Contain(Vector2i.Zero), "A partial reservation must be requeued after shutdown.");
            }
            void Finish(IEnumerator<bool> steps)
            {
                using (steps) while (steps.MoveNext()) { }
            }

            LoadedChunksForFixture(biome).Add(Vector2i.Zero);
            var loading = Load();
            while (!biome.LoadedEntities.Any(chunk => chunk.Key == Vector2i.Zero && chunk.Value.Count >= 8))
                Assert.That(loading.MoveNext(), Is.True);
            var original = biome.LoadedEntities[Vector2i.Zero].Single(p => p.Value == Vector2i.Zero).Key;
            var prototype = entities.GetComponent<MetaDataComponent>(original).EntityPrototype!.ID;
            var renamed = biome.LoadedEntities[Vector2i.Zero].Single(p => p.Value == new Vector2i(0, 1)).Key;
            entities.System<MetaDataSystem>().SetEntityName(renamed, "changed during streaming");
            var mined = new Vector2i(0, 2);
            entities.DeleteEntity(biome.LoadedEntities[Vector2i.Zero].Single(p => p.Value == mined).Key);
            Interrupt("_loadingChunk", loading);
            LoadedChunksForFixture(biome).Add(Vector2i.Zero);
            Finish(Load());
            Assert.That(biome.LoadedEntities[Vector2i.Zero], Has.Count.EqualTo(63));
            Assert.That(biome.LoadedEntities[Vector2i.Zero].Values.Distinct().Count(), Is.EqualTo(63));
            Assert.That(biome.LoadedEntities[Vector2i.Zero].Values, Does.Not.Contain(mined));

            var unloading = Unload();
            while (entities.EntityExists(original))
                Assert.That(unloading.MoveNext(), Is.True);
            Interrupt("_unloadingChunk", unloading);
            LoadedChunksForFixture(biome).Add(Vector2i.Zero);
            Finish(Load());
            var restored = biome.LoadedEntities[Vector2i.Zero];
            Assert.That(restored, Has.Count.EqualTo(63));
            Assert.That(restored.Values.Distinct().Count(), Is.EqualTo(63));
            var replacement = restored.Single(p => p.Value == Vector2i.Zero).Key;
            Assert.That(entities.GetComponent<MetaDataComponent>(replacement).EntityPrototype!.ID, Is.EqualTo(prototype));
            Assert.That(entities.GetComponent<MetaDataComponent>(renamed).EntityName, Is.EqualTo("changed during streaming"));
            Finish(Unload());
            Assert.That(entities.System<SharedMapSystem>().GetTileRef(map, grid, Vector2i.Zero).Tile.IsEmpty, Is.True,
                "Cancelling an unload must not mark untouched generated floors as authored terrain.");
            Assert.That(entities.EntityExists(renamed), Is.True);
            entities.DeleteEntity(map);
        });
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

        await WaitForStreaming(pair, () => biome.LoadedEntities.Any(chunk => chunk.Key == Vector2i.Zero &&
            chunk.Value.Values.Contains(renamed)), "The edited rock must finish streaming before the interaction.");
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

        await WaitForStreaming(pair, () => biome.LoadedChunks.Count == 0, "Edited terrain must finish unloading.");
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

        await WaitForStreaming(pair, () => biome.LoadedEntities.Any(chunk => chunk.Key == Vector2i.Zero &&
            chunk.Value.Values.Contains(untouched)), "Unmodified terrain must return after re-entering its window.");
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

    [Test]
    public async Task RemovingBiomeDropsOnlyItsMarkerBacklogAndCancelsItsScan()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var entities = pair.Server.EntMan;
        await pair.Server.WaitAssertion(() =>
        {
            var (firstMap, _, firstBiome, _) = SpawnPlanet(entities, pair.Server.ProtoMan.Index(EmptyBiome));
            var (otherMap, _, otherBiome, _) = SpawnPlanet(entities, pair.Server.ProtoMan.Index(EmptyBiome));
            var biomes = entities.System<BiomeSystem>();
            biomes.AddMarkerLayer(firstMap, firstBiome, Marker);
            biomes.AddMarkerLayer(otherMap, otherBiome, Marker);
            var area = new Box2(1, 1, 255, 127);
            biomes.Preload(firstMap, firstBiome, area);
            biomes.Preload(otherMap, otherBiome, area);

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(BiomeSystem);
            type.GetMethod("QueueMarkerPreloads", flags)!.Invoke(biomes, null);
            var pending = (IDictionary) type.GetField("_pendingMarkerPreloads", flags)!.GetValue(biomes)!;
            object[] RequestsFor(BiomeComponent biome) => pending.Keys.Cast<object>()
                .Where(request => ReferenceEquals(request.GetType().GetProperty("Biome")!.GetValue(request), biome)).ToArray();
            var firstRequests = RequestsFor(firstBiome);
            var otherRequests = RequestsFor(otherBiome);
            Assert.That(firstRequests.Length, Is.GreaterThanOrEqualTo(2));
            Assert.That(otherRequests.Length, Is.GreaterThanOrEqualTo(2));

            var activeRequest = firstRequests[0];
            pending.Remove(activeRequest);
            var startScan = type.GetMethod("StartMarkerScan", flags)!;
            var layer = pair.Server.ProtoMan.Index<BiomeMarkerLayerPrototype>(Marker);
            void StartScan() => startScan.Invoke(biomes, [activeRequest, layer, 1, false]);
            var scanField = type.GetField("_markerScan", flags)!;
            StartScan();
            Assert.That(scanField.GetValue(biomes), Is.Not.Null);

            // A stopped component may restart. Pending requests must survive this boundary.
            var shutdown = new ComponentShutdown();
            entities.EventBus.RaiseComponentEvent(firstMap, firstBiome, ref shutdown);
            Assert.That(scanField.GetValue(biomes), Is.Null);
            Assert.That(RequestsFor(firstBiome), Has.Length.EqualTo(firstRequests.Length - 1));
            Assert.That(RequestsFor(otherBiome), Is.EquivalentTo(otherRequests));

            var prepare = type.GetMethod("PrepareChunkMarkers", flags)!;
            Assert.That(prepare.Invoke(biomes, [firstBiome, Vector2i.Zero]), Is.False);
            Assert.That(scanField.GetValue(biomes), Is.Not.Null);
            entities.DeleteEntity(firstMap);
            Assert.That(scanField.GetValue(biomes), Is.Null);
            Assert.That(RequestsFor(firstBiome), Is.Empty);
            Assert.That(RequestsFor(otherBiome), Is.EquivalentTo(otherRequests));
            Assert.That(entities.EntityExists(otherMap), Is.True);
            entities.DeleteEntity(otherMap);
            Assert.That(pending.Count, Is.Zero);
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

    private static async Task WaitForStreaming(TestPair pair, Func<bool> ready, string message)
    {
        for (var tick = 0; tick < 600; tick++)
        {
            var complete = false;
            await pair.Server.WaitAssertion(() => complete = ready());
            if (complete) return;
            await pair.RunTicksSync(1);
        }
        Assert.Fail(message);
    }
}

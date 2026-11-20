using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server.GameTicking;
using Content.Shared.GameTicking.Components;
using Content.Server._NF.StationEvents.Components;
using Content.Server._NF.StationEvents.Events;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Spawners;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class IncrementalGridLoadingTests
{
    private static readonly ResPath FixturePath = new("/incremental-grid-regression.yml");

    private static EntityUid CreateFixture(EntityManager em, ITileDefinitionManager definitions, int timerCount = 96)
    {
        var maps = em.System<SharedMapSystem>();
        var map = maps.CreateMap(out var id, runMapInit: false);
        var grid = maps.CreateGridEntity(id);
        var floor = new Tile(definitions["Plating"].TileId);
        maps.SetTiles(grid, Enumerable.Range(0, 64).Select(i => (new Vector2i(i % 8, i / 8), floor)).ToList());
        var metadata = em.System<MetaDataSystem>();
        var box = em.SpawnEntity("CrateGeneric", new EntityCoordinates(grid, new Vector2(2.5f, 2.5f)));
        metadata.SetEntityName(box, "incremental-box");
        var item = em.SpawnEntity("SheetSteel1", new EntityCoordinates(grid, new Vector2(2.5f, 2.5f)));
        metadata.SetEntityName(item, "incremental-contained-item");
        var container = em.System<SharedContainerSystem>().EnsureContainer<Container>(box, "incremental-links");
        Assert.That(em.System<SharedContainerSystem>().Insert(item, container), Is.True);
        for (var i = 0; i < timerCount; i++)
        {
            var timer = em.SpawnEntity(null, new EntityCoordinates(grid, new Vector2(i % 8 + .5f, i / 8 % 8 + .5f)));
            em.AddComponent<TimedDespawnComponent>(timer).Lifetime = 30;
        }
        Assert.That(em.System<MapLoaderSystem>().TrySaveGrid(grid, FixturePath,
            new SerializationOptions { ExpectPreInit = true }), Is.True);
        return map;
    }

    [Test]
    public async Task IncrementalLoadPreservesTilesContainersAndPausedLifetimes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var em = pair.Server.EntMan;
        var maps = em.System<SharedMapSystem>();
        var loader = em.System<MapLoaderSystem>();
        EntityUid source = default, synchronousMap = default, incrementalMap = default;
        MapLoaderSystem.IncrementalGridLoad job = default!;
        Entity<MapGridComponent> synchronous = default;
        await pair.Server.WaitAssertion(() =>
        {
            source = CreateFixture(em, pair.Server.ResolveDependency<ITileDefinitionManager>());
            synchronousMap = maps.CreateMap(out var syncId, runMapInit: false);
            Assert.That(loader.TryLoadGrid(syncId, FixturePath, out var grid), Is.True);
            synchronous = grid!.Value;
            maps.InitializeMap(synchronousMap, unpause: false);
            incrementalMap = maps.CreateMap(out var incrementalId, runMapInit: false);
            job = loader.BeginIncrementalGridLoad(incrementalId, FixturePath);
        });
        var finished = false;
        var slices = 0;
        while (!finished && slices++ < 1000)
        {
            await pair.Server.WaitAssertion(() =>
            {
                for (var i = 0; i < 16; i++)
                    if (!job.MoveNext()) { finished = true; break; }
                // Every ordinary update sees only paused partial entities.
                foreach (var uid in job.Entities)
                    Assert.That(em.GetComponent<MetaDataComponent>(uid).EntityPaused, Is.True);
            });
            if (!finished) await pair.Server.WaitRunTicks(1);
        }
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(finished, Is.True);
            Assert.That(slices, Is.GreaterThan(10));
            Assert.That(job.RequiresSynchronousLoad, Is.False);
            Assert.That(job.Grid, Is.Not.Null);
            var actual = job.Grid!.Value;
            Assert.That(maps.GetAllTiles(actual, actual.Comp).Select(t => t.GridIndices),
                Is.EquivalentTo(maps.GetAllTiles(synchronous, synchronous.Comp).Select(t => t.GridIndices)));
            EntityUid Find(EntityUid grid, string name)
            {
                var query = em.AllEntityQueryEnumerator<MetaDataComponent, TransformComponent>();
                while (query.MoveNext(out var uid, out var meta, out var transform))
                    if (transform.GridUid == grid && meta.EntityName == name) return uid;
                Assert.Fail(name); return default;
            }
            var box = Find(actual, "incremental-box");
            var item = Find(actual, "incremental-contained-item");
            Assert.That(em.System<SharedContainerSystem>().GetContainer(box, "incremental-links").ContainedEntities, Does.Contain(item));
            var timers = job.Entities.Where(uid => em.HasComponent<TimedDespawnComponent>(uid)).ToArray();
            Assert.That(timers, Has.Length.EqualTo(96));
            foreach (var uid in timers)
            {
                Assert.That(em.GetComponent<TimedDespawnComponent>(uid).Lifetime, Is.EqualTo(30));
                Assert.That(em.GetComponent<MetaDataComponent>(uid).EntityLifeStage, Is.EqualTo(EntityLifeStage.MapInitialized));
            }
            job.Dispose();
            em.DeleteEntity(source);
            em.DeleteEntity(synchronousMap);
            em.DeleteEntity(incrementalMap);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(GridLoadPhase.Allocate)]
    [TestCase(GridLoadPhase.Startup)]
    [TestCase(GridLoadPhase.MapInit)]
    public async Task CancellationCanCleanEveryAllocatedEntity(GridLoadPhase stopAt)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var em = pair.Server.EntMan;
        var maps = em.System<SharedMapSystem>();
        MapLoaderSystem.IncrementalGridLoad job = default!;
        EntityUid source = default, staging = default;
        await pair.Server.WaitAssertion(() =>
        {
            source = CreateFixture(em, pair.Server.ResolveDependency<ITileDefinitionManager>());
            staging = maps.CreateMap(out var id, runMapInit: false);
            job = em.System<MapLoaderSystem>().BeginIncrementalGridLoad(id, FixturePath);
        });
        var reached = false;
        for (var tick = 0; tick < 1000 && !reached; tick++)
        {
            await pair.Server.WaitAssertion(() =>
            {
                for (var i = 0; i < 8; i++)
                {
                    Assert.That(job.MoveNext(), Is.True);
                    if (job.Phase == stopAt) { reached = true; break; }
                }
            });
            if (!reached) await pair.Server.WaitRunTicks(1);
        }
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(reached, Is.True);
            var allocated = job.Entities.ToArray();
            Assert.That(allocated, Is.Not.Empty);
            job.Dispose();
            foreach (var uid in allocated) if (em.EntityExists(uid)) em.DeleteEntity(uid);
            em.DeleteEntity(staging);
            Assert.That(allocated.Any(em.EntityExists), Is.False);
            em.DeleteEntity(source);
        });
        await pair.Server.WaitRunTicks(2);
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EndingRuleDuringLoadingRetiresStagingAndOrphanedEntities()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var em = pair.Server.EntMan;
        var maps = em.System<SharedMapSystem>();
        var ticker = em.System<GameTicker>();
        var system = em.System<BluespaceErrorRule>();
        var oldDefault = ticker.DefaultMap;
        EntityUid source = default, destination = default, rule = default;
        EntityUid[] allocated = [];
        await pair.Server.WaitAssertion(() =>
        {
            source = CreateFixture(em, pair.Server.ResolveDependency<ITileDefinitionManager>(), timerCount: 4096);
            destination = maps.CreateMap(out var id);
            typeof(GameTicker).GetProperty(nameof(GameTicker.DefaultMap))!.SetValue(ticker, id);
            rule = ticker.AddGameRule("BluespaceSalvage");
            em.GetComponent<GameRuleComponent>(rule).Delay = null;
            foreach (var group in em.GetComponent<BluespaceErrorRuleComponent>(rule).Groups.Values.OfType<BluespaceGridSpawnGroup>())
            {
                group.Paths.Clear(); group.Paths.Add(FixturePath); group.MinCount = group.MaxCount = 1;
            }
            Assert.That(ticker.StartGameRule(rule), Is.True);
        });
        var reached = false;
        for (var i = 0; i < 1000 && !reached; i++)
        {
            await pair.Server.WaitAssertion(() =>
            {
                system.Update(0);
                var query = em.AllEntityQueryEnumerator<MetaDataComponent>();
                var boxes = 0;
                while (query.MoveNext(out _, out var meta)) if (meta.EntityName == "incremental-box") boxes++;
                reached = boxes > 1;
                if (!reached) return;
                Assert.That(ticker.EndGameRule(rule), Is.True);
                allocated = ((Queue<EntityUid>) typeof(BluespaceErrorRule)
                    .GetField("_cancelledLoadEntities", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system)!).ToArray();
                Assert.That(allocated, Is.Not.Empty);
            });
            if (!reached) await Task.Delay(1);
        }
        Assert.That(reached, Is.True);
        await pair.Server.WaitRunTicks(100);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(allocated.Any(em.EntityExists), Is.False);
            Assert.That(em.GetComponent<BluespaceErrorRuleComponent>(rule).MapsUid.All(id => !maps.MapExists(id)), Is.True);
            Assert.That(em.EntityExists(source) && em.EntityExists(destination), Is.True);
            em.DeleteEntity(rule); em.DeleteEntity(source); em.DeleteEntity(destination);
            typeof(GameTicker).GetProperty(nameof(GameTicker.DefaultMap))!.SetValue(ticker, oldDefault);
        });
        await pair.CleanReturnAsync();
    }
}






#nullable enable
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server.Explosion.Components;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Shared._DeepLagoon.ShuttleParking;
using Content.Shared._Mono.ShipRepair.Components;
using Robust.Shared.Maths;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class IncrementalParkedLoadTests
{
    private const string ParentName = "incremental-parked-parent";
    private const string ContentName = "incremental-parked-content";
    private static readonly TimeSpan Remaining = TimeSpan.FromSeconds(17);

    // Persisted entities already ran their initial MapInit. Repeating it may
    // produce inventories/spawners again, even if their serialized state is valid.
    private sealed class ParkedMapInitProbeSystem : EntitySystem
    {
        public readonly HashSet<EntityUid> Reinitialized = new();
        public override void Initialize()
        {
            SubscribeLocalEvent<MetaDataComponent, MapInitEvent>((uid, meta, _) =>
            {
                if (!meta.EntityName.StartsWith("incremental-parked-", StringComparison.Ordinal)) return;
                Reinitialized.Add(uid);
                if (meta.EntityName == ParentName)
                {
                    var extra = Spawn(null, new EntityCoordinates(uid, Vector2.Zero));
                    EntityManager.System<MetaDataSystem>().SetEntityName(extra, "incremental-parked-duplicate");
                }
            });
        }
    }

    [Test]
    public async Task LivePausedSnapshotKeepsPerEntityTimerEpochAndDoesNotRepeatMapInit()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var source = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        var maps = entities.System<SharedMapSystem>();
        var clock = pair.Server.ResolveDependency<IGameTiming>();
        MapId staged = default;
        MapLoaderSystem.IncrementalGridLoad load = null!;
        Guid identity = default;

        await pair.Server.WaitAssertion(() =>
        {
            var yaml = Snapshot(entities, source.Grid.Owner, source.MapId, clock, out identity);
            maps.CreateMap(out staged, runMapInit: false);
            maps.SetPaused(staged, true);
            load = entities.System<MapLoaderSystem>().BeginIncrementalGridLoad(staged, () => new StringReader(yaml),
                new DeserializationOptions { LogInvalidEntities = false }, allowPostMapInit: true);
        });
        using (load)
        {
            var more = true;
            for (var i = 0; i < 2000 && more; i++)
            {
                await pair.Server.WaitPost(() => more = load.MoveNext());
                await pair.RunTicksSync(1);
            }

            EntityUid parent = default;
            await pair.Server.WaitAssertion(() =>
            {
                Assert.That(load.Completed, Is.True, "Parsing and cooperative loading must finish within the bounded run.");
                Assert.That(load.RequiresSynchronousLoad, Is.False);
                Assert.That(load.Grid, Is.Not.Null);
                var grid = load.Grid!.Value.Owner;
                Assert.That(load.Entities.Count(uid => entities.HasComponent<MapGridComponent>(uid)), Is.EqualTo(1));
                Assert.That(entities.GetComponent<ParkingShuttleComponent>(grid).Identity, Is.EqualTo(identity));
                Assert.That(maps.IsPaused(staged), Is.True);
                parent = load.Entities.Single(uid => entities.GetComponent<MetaDataComponent>(uid).EntityName == ParentName);
                var content = load.Entities.Single(uid => entities.GetComponent<MetaDataComponent>(uid).EntityName == ContentName);
                Assert.That(entities.GetComponent<TransformComponent>(content).ParentUid, Is.EqualTo(parent));
                Assert.That(entities.GetComponent<TransformComponent>(parent).ParentUid, Is.EqualTo(grid));
                var meta = entities.GetComponent<MetaDataComponent>(parent);
                var trigger = entities.GetComponent<TriggerOnProximityComponent>(parent);
                Assert.That(meta.EntityPaused, Is.True);
                var pausedAt = clock.CurTime - entities.System<MetaDataSystem>().GetPauseTime(parent, meta);
                Assert.That(trigger.NextTrigger - pausedAt, Is.EqualTo(Remaining));
                Assert.That(trigger.NextVisualUpdate, Is.EqualTo(TimeSpan.MaxValue));
                Assert.That(entities.System<ParkedMapInitProbeSystem>().Reinitialized, Is.Empty);
                Assert.That(load.Entities.Any(uid => entities.GetComponent<MetaDataComponent>(uid).EntityName == "incremental-parked-duplicate"), Is.False);
            });
            await pair.RunTicksSync(25);
            await pair.Server.WaitAssertion(() =>
            {
                maps.SetPaused(staged, false);
                var trigger = entities.GetComponent<TriggerOnProximityComponent>(parent);
                Assert.That(entities.GetComponent<MetaDataComponent>(parent).EntityPaused, Is.False);
                Assert.That((trigger.NextTrigger - clock.CurTime).TotalSeconds, Is.EqualTo(17).Within(.001),
                    "The remaining deadline must exclude all ticks spent reading, starting and waiting on the private paused map.");
                Assert.That(trigger.NextVisualUpdate, Is.EqualTo(TimeSpan.MaxValue), "The no-update sentinel must not overflow on unpause.");
                entities.DeleteEntity(maps.GetMap(staged));
            });
        }
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task IdleParkingWriterRoundTripsLiveOwnershipNestedContentAndRepairBlueprint()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var source = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        var maps = entities.System<SharedMapSystem>();
        var clock = pair.Server.ResolveDependency<IGameTiming>();
        IEnumerator writer = null!;
        MapLoaderSystem.IncrementalGridLoad load = null!;
        Guid identity = default, owner = default, blueprintVersion = default, toolIdentity = default;
        EntityUid tool = default;
        Dictionary<Guid, EntityUid> originals = null!;
        Dictionary<Guid, EntityUid> originalsReference = null!;
        ShipRepairDataComponent blueprint = null!;
        string? yaml = null;
        MapId staged = default;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await pair.Server.WaitAssertion(() =>
        {
            for (var x = 0; x <= 17; x++) maps.SetTile(source.Grid, new Vector2i(x, 0), source.Tile.Tile);
            tool = entities.SpawnEntity("Crowbar", new EntityCoordinates(source.Grid, new Vector2(2.5f, .5f)));
            Assert.That(entities.GetComponent<TransformComponent>(tool).GridUid, Is.EqualTo(source.Grid.Owner));
            Assert.That(entities.GetComponent<TransformComponent>(tool).ParentUid, Is.EqualTo(source.Grid.Owner));
            identity = PrepareSnapshot(entities, source.Grid.Owner, source.MapId, clock);
            var ship = entities.GetComponent<ParkingShuttleComponent>(source.Grid);
            owner = ship.OwnerAccount;
            ship.ParkingProfile = 4242;
            ship.OriginFaction = "TSF";
            originalsReference = ship.OriginalItems;
            originals = new Dictionary<Guid, EntityUid>(ship.OriginalItems);
            Assert.That(entities.HasComponent<ParkingOriginalComponent>(tool), Is.True, "A tool aboard the purchased grid must be registered as original equipment.");
            toolIdentity = entities.GetComponent<ParkingOriginalComponent>(tool).Identity;
            blueprint = entities.EnsureComponent<ShipRepairDataComponent>(source.Grid);
            blueprint.EntityPalette.Add("Crowbar");
            blueprint.SnapshotVersion = blueprintVersion = Guid.NewGuid();
            blueprint.SnapshotReadyAt = 123456789;
            blueprint.Chunks[Vector2i.Zero] = new ShipRepairChunk
            {
                Tiles = Enumerable.Repeat((int)source.Tile.Tile.TypeId, 25).ToArray(),
                NextUid = 4,
                Entities = new()
                {
                    [2] = new ShipRepairEntitySpecifier
                    {
                        ProtoIndex = 0, LocalPosition = new Vector2(2.5f, .5f), Rotation = Angle.FromDegrees(30),
                        OriginalEntity = entities.GetNetEntity(tool), ParkingOriginalIdentity = toolIdentity,
                        ParkingBaseline = "Crowbar", PaidCapital = 123,
                    },
                    [3] = new ShipRepairEntitySpecifier
                    {
                        ProtoIndex = 0, LocalPosition = new Vector2(3.5f, .5f), ParkingBaseline = "Crowbar",
                    },
                },
            };
            var parking = entities.System<ParkingSystem>();
            writer = ((IEnumerable)typeof(ParkingSystem).GetMethod("SerializeSnapshotSteps", flags)!
                .Invoke(parking, [source.Grid.Owner, false, true, true])!).GetEnumerator();
        });
        var advances = 0;
        try
        {
            for (; advances < 6000 && yaml == null; advances++)
            {
                await pair.Server.WaitAssertion(() =>
                {
                    Assert.That(writer.MoveNext(), Is.True, "The real idle writer must produce a complete snapshot.");
                    if (writer.Current is { } result)
                        yaml = (string)result.GetType().GetProperty("Data")!.GetValue(result)!;
                });
                await pair.RunTicksSync(1); // Pumps writer preparation and background YAML encoder continuations.
            }
            await pair.Server.WaitAssertion(() =>
            {
                Assert.That(yaml, Is.Not.Null, "Idle capture must finish within the bounded tick budget.");
                Assert.That(advances, Is.GreaterThan(16), "This must exercise the staged writer, not the monolithic helper.");
                var ship = entities.GetComponent<ParkingShuttleComponent>(source.Grid);
                Assert.That(ship.OriginalItems, Is.SameAs(originalsReference));
                Assert.That(ship.OriginalItems, Is.EquivalentTo(originals));
                Assert.That(blueprint.Chunks[Vector2i.Zero].Entities[2].OriginalEntity, Is.EqualTo(entities.GetNetEntity(tool)),
                    "Preparing component overrides must not alter live repair references between ticks.");
                maps.CreateMap(out staged, runMapInit: false);
                maps.SetPaused(staged, true);
                load = entities.System<MapLoaderSystem>().BeginIncrementalGridLoad(staged, () => new StringReader(yaml!),
                    new DeserializationOptions { LogInvalidEntities = false }, allowPostMapInit: true);
            });
        }
        finally { await pair.Server.WaitPost(() => (writer as IDisposable)?.Dispose()); }
        using (load)
        {
            var more = true;
            for (var tick = 0; tick < 2000 && more; tick++)
            {
                await pair.Server.WaitPost(() => more = load.MoveNext());
                await pair.RunTicksSync(1);
            }
            await pair.Server.WaitAssertion(() =>
            {
                Assert.That(load.Completed, Is.True);
                Assert.That(load.RequiresSynchronousLoad, Is.False);
                Assert.That(load.Grid, Is.Not.Null);
                var grid = load.Grid!.Value.Owner;
                var ship = entities.GetComponent<ParkingShuttleComponent>(grid);
                Assert.That(ship.Identity, Is.EqualTo(identity));
                Assert.That(ship.OwnerAccount, Is.EqualTo(owner));
                Assert.That(ship.ParkingProfile, Is.EqualTo(4242));
                Assert.That(ship.OriginFaction, Is.EqualTo("TSF"));
                Assert.That(ship.Parked, Is.True);
                Assert.That(ship.OriginalItems.Keys, Is.EquivalentTo(originals.Keys));
                var restoredTool = ship.OriginalItems[toolIdentity];
                Assert.That(restoredTool, Is.Not.EqualTo(tool));
                Assert.That(entities.GetComponent<MetaDataComponent>(restoredTool).EntityPrototype!.ID, Is.EqualTo("Crowbar"));
                var parent = load.Entities.Single(uid => entities.GetComponent<MetaDataComponent>(uid).EntityName == ParentName);
                var content = load.Entities.Single(uid => entities.GetComponent<MetaDataComponent>(uid).EntityName == ContentName);
                Assert.That(entities.GetComponent<TransformComponent>(parent).ParentUid, Is.EqualTo(grid));
                Assert.That(entities.GetComponent<TransformComponent>(content).ParentUid, Is.EqualTo(parent));
                var restored = entities.GetComponent<ShipRepairDataComponent>(grid);
                Assert.That(restored.SnapshotVersion, Is.EqualTo(blueprintVersion));
                Assert.That(restored.SnapshotReadyAt, Is.EqualTo(123456789));
                Assert.That(restored.EntityPalette, Is.EqualTo(blueprint.EntityPalette));
                var chunk = restored.Chunks[Vector2i.Zero];
                Assert.That(chunk.Tiles, Is.EqualTo(blueprint.Chunks[Vector2i.Zero].Tiles));
                Assert.That(chunk.NextUid, Is.EqualTo(4));
                Assert.That(chunk.Entities.Keys, Is.EquivalentTo(new[] { 2, 3 }));
                Assert.That(chunk.Entities[2].OriginalEntity, Is.EqualTo(entities.GetNetEntity(restoredTool)));
                Assert.That(chunk.Entities[2].ParkingOriginalIdentity, Is.EqualTo(toolIdentity));
                Assert.That(chunk.Entities[2].ParkingBaseline, Is.EqualTo("Crowbar"));
                Assert.That(chunk.Entities[2].PaidCapital, Is.EqualTo(123));
                Assert.That(chunk.Entities[2].LocalPosition, Is.EqualTo(new Vector2(2.5f, .5f)));
                Assert.That(chunk.Entities[2].Rotation, Is.EqualTo(Angle.FromDegrees(30)));
                Assert.That(chunk.Entities[3].OriginalEntity, Is.Null, "Missing objects must remain repair ghosts.");
                Assert.That(maps.IsPaused(staged), Is.True);
                var meta = entities.GetComponent<MetaDataComponent>(parent);
                var trigger = entities.GetComponent<TriggerOnProximityComponent>(parent);
                var pausedAt = clock.CurTime - entities.System<MetaDataSystem>().GetPauseTime(parent, meta);
                Assert.That(trigger.NextTrigger - pausedAt, Is.EqualTo(Remaining));
                Assert.That(entities.System<ParkedMapInitProbeSystem>().Reinitialized, Is.Empty);
                entities.DeleteEntity(maps.GetMap(staged));
            });
        }
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DefaultIncrementalContractRejectsLiveSnapshotBeforeAllocatingEntities()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var source = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        var maps = entities.System<SharedMapSystem>();
        MapId staged = default;
        MapLoaderSystem.IncrementalGridLoad load = null!;
        await pair.Server.WaitAssertion(() =>
        {
            var yaml = Snapshot(entities, source.Grid.Owner, source.MapId, pair.Server.ResolveDependency<IGameTiming>(), out _);
            maps.CreateMap(out staged, runMapInit: false);
            maps.SetPaused(staged, true);
            load = entities.System<MapLoaderSystem>().BeginIncrementalGridLoad(staged, () => new StringReader(yaml),
                new DeserializationOptions { LogInvalidEntities = false });
        });
        using (load)
        {
            var more = true;
            for (var i = 0; i < 2000 && more; i++)
            {
                await pair.Server.WaitPost(() => more = load.MoveNext());
                await pair.RunTicksSync(1);
            }
            await pair.Server.WaitAssertion(() =>
            {
                Assert.That(load.Completed, Is.True);
                Assert.That(load.RequiresSynchronousLoad, Is.True);
                Assert.That(load.Entities, Is.Empty);
                Assert.That(load.Grid, Is.Null);
                Assert.That(entities.System<ParkedMapInitProbeSystem>().Reinitialized, Is.Empty);
                entities.DeleteEntity(maps.GetMap(staged));
            });
        }
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CancelledLoadDoesNotPublishASecondShipAndCallerCanDeleteAllAllocatedEntities()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var source = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        var maps = entities.System<SharedMapSystem>();
        MapId staged = default;
        MapLoaderSystem.IncrementalGridLoad load = null!;
        Guid identity = default;
        await pair.Server.WaitAssertion(() =>
        {
            var yaml = Snapshot(entities, source.Grid.Owner, source.MapId, pair.Server.ResolveDependency<IGameTiming>(), out identity);
            maps.CreateMap(out staged, runMapInit: false);
            maps.SetPaused(staged, true);
            load = entities.System<MapLoaderSystem>().BeginIncrementalGridLoad(staged, () => new StringReader(yaml),
                new DeserializationOptions { LogInvalidEntities = false }, allowPostMapInit: true);
        });
        var allocatedGrid = false;
        for (var i = 0; i < 2000 && !allocatedGrid; i++)
        {
            await pair.Server.WaitPost(() =>
            {
                Assert.That(load.MoveNext(), Is.True);
                allocatedGrid = load.Entities.Any(uid => entities.HasComponent<MapGridComponent>(uid));
            });
            await pair.RunTicksSync(1);
        }
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(allocatedGrid, Is.True);
            Assert.That(load.Completed, Is.False);
            Assert.That(load.Grid, Is.Null, "A partial allocation must not become a publishable loaded vessel.");
            var allocated = load.Entities.ToArray();
            load.Dispose();
            Assert.Throws<ObjectDisposedException>(() => load.MoveNext());
            // Dispose cancels work, not ECS ownership. Unparented pre-hierarchy entities
            // also belong to the caller and cannot be cleaned by deleting only the target map.
            foreach (var uid in allocated)
                if (entities.EntityExists(uid)) entities.DeleteEntity(uid);
            entities.DeleteEntity(maps.GetMap(staged));
            Assert.That(allocated.Any(entities.EntityExists), Is.False);
            var parking = entities.System<ParkingSystem>();
            var active = (Dictionary<Guid, EntityUid>)typeof(ParkingSystem)
                .GetField("_activeShips", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(parking)!;
            Assert.That(active[identity], Is.EqualTo(source.Grid.Owner));
        });
        await pair.CleanReturnAsync();
    }

    private static string Snapshot(IEntityManager entities, EntityUid grid, MapId map, IGameTiming clock, out Guid identity)
    {
        identity = PrepareSnapshot(entities, grid, map, clock);
        return entities.System<ParkingSystem>().SerializeSnapshot(grid);
    }

    private static Guid PrepareSnapshot(IEntityManager entities, EntityUid grid, MapId map, IGameTiming clock)
    {
        var probe = entities.System<ParkedMapInitProbeSystem>();
        var parent = entities.SpawnEntity(null, new EntityCoordinates(grid, new Vector2(.5f)));
        var content = entities.SpawnEntity(null, new EntityCoordinates(parent, new Vector2(.25f)));
        var names = entities.System<MetaDataSystem>();
        names.SetEntityName(parent, ParentName);
        names.SetEntityName(content, ContentName);
        var trigger = entities.EnsureComponent<TriggerOnProximityComponent>(parent);
        trigger.Enabled = false;
        var parking = entities.System<ParkingSystem>();
        parking.RegisterPurchasedShip(grid, Guid.NewGuid(), 1000, "", "Test Captain");
        entities.GetComponent<ParkingShuttleComponent>(grid).Parked = true;
        entities.System<SharedMapSystem>().SetPaused(map, true);
        var meta = entities.GetComponent<MetaDataComponent>(parent);
        Assert.That(meta.EntityPaused, Is.True);
        var pausedAt = clock.CurTime - entities.System<MetaDataSystem>().GetPauseTime(parent, meta);
        trigger.NextTrigger = pausedAt + Remaining;
        trigger.NextVisualUpdate = TimeSpan.MaxValue;
        var identity = entities.GetComponent<ParkingShuttleComponent>(grid).Identity;
        Assert.That(probe.Reinitialized, Is.Empty);
        return identity;
    }
}

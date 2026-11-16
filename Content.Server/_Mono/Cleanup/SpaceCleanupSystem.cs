using System.Numerics;
using Content.Server.Cargo.Systems;
using Content.Server.NPC.HTN;
using Content.Server.Shuttles.Components;
using Content.Shared._Mono.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Mind.Components;
using Content.Shared.Physics;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;
using System.Reflection;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server._Mono.Cleanup;

/// <summary>
///     Deletes entities eligible for deletion.
/// </summary>
public sealed partial class SpaceCleanupSystem : BaseCleanupSystem<PhysicsComponent>
{
    [Dependency] private CleanupHelperSystem _cleanup = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IGameTiming _timing = default!;
    private object _manifold = default!;
    private MethodInfo _testOverlap = default!;
    [Dependency] private PricingSystem _pricing = default!;
    private SharedMapSystem _map => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private float _maxDistance;
    private float _maxGridDistance;
    private float _maxPrice;

    private EntityQuery<CleanupImmuneComponent> _immuneQuery;
    private EntityQuery<FixturesComponent> _fixQuery;
    private EntityQuery<HTNComponent> _htnQuery;
    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<MindContainerComponent> _mindQuery;
    private EntityQuery<PhysicsComponent> _physQuery;

    private const int MaxCandidatesPerTick = 64;
    private const int MaxPeriodicCandidatesPerTick = 16;
    private const int MaxSweepLookupsPerTick = 4;
    private static readonly long WorkBudget = Stopwatch.Frequency / 500; // 2 ms between operations.
    private static readonly TimeSpan TimingLogInterval = TimeSpan.FromSeconds(5);

    private readonly record struct Sweep(EntityCoordinates Coordinates, float Radius, float Aggression);

    private readonly PriorityQueue<Sweep, (TimeSpan Due, long Sequence)> _sweepQueue = new();
    private long _sweepSequence;
    private readonly HashSet<Entity<PhysicsComponent>> _sweepEnts = new();
    private HashSet<Entity<PhysicsComponent>>.Enumerator _sweepCandidates;
    private int _remainingSweepCandidates;
    private float _activeAggression;
    private GameTick _sweepLookupTick;
    private MapId _sweepMap;
    private Box2 _sweepBounds;
    private TimeSpan _nextTimingLog;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundCleanup);

        // this queries over literally everything with PhysicsComponent so has to have big interval
        _cleanupInterval = TimeSpan.FromSeconds(600);

        _immuneQuery = GetEntityQuery<CleanupImmuneComponent>();
        _fixQuery = GetEntityQuery<FixturesComponent>();
        _htnQuery = GetEntityQuery<HTNComponent>();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _mindQuery = GetEntityQuery<MindContainerComponent>();
        _physQuery = GetEntityQuery<PhysicsComponent>();

        Subs.CVar(_cfg, MonoCVars.CleanupMaxGridDistance, val => _maxGridDistance = val, true);
        Subs.CVar(_cfg, MonoCVars.SpaceCleanupDistance, val => _maxDistance = val, true);
        Subs.CVar(_cfg, MonoCVars.SpaceCleanupMaxValue, val => _maxPrice = val, true);

        var manifoldType = typeof(SharedMapSystem).Assembly.GetType("Robust.Shared.Physics.Collision.IManifoldManager");
        if (manifoldType != null)
        {
            _manifold = IoCManager.ResolveType(manifoldType);
            var testOverlapMethod = manifoldType.GetMethod("TestOverlap");
            if (testOverlapMethod != null)
                _testOverlap = testOverlapMethod.MakeGenericMethod(typeof(IPhysShape), typeof(PhysShapeCircle));
        }
    }

    protected override bool ShouldEntityCleanup(EntityUid uid)
    {
        return ShouldEntityCleanup(uid, 1f);
    }

    private bool ShouldEntityCleanup(EntityUid uid, float aggression)
    {
        var xform = Transform(uid);

        var isStuck = false;

        var price = 0f;

        return !_gridQuery.HasComp(uid)
            && (xform.ParentUid == xform.MapUid // don't delete if on grid
                || (isStuck |= GetWallStuck((uid, xform)))) // or wall-stuck
            && !_htnQuery.HasComp(uid) // handled by MobCleanupSystem
            && !_immuneQuery.HasComp(uid) // handled by GridCleanupSystem
            && !_mindQuery.HasComp(uid) // no deleting anything that can have a mind - should be handled by MobCleanupSystem anyway
            && (price = (float)_pricing.GetPrice(uid)) <= _maxPrice
            && (isStuck
                || !_cleanup.HasNearbyGrids(xform.Coordinates, _maxGridDistance * aggression * MathF.Sqrt(price / _maxPrice))
                    && !_cleanup.HasNearbyPlayers(xform.Coordinates, _maxDistance * aggression * MathF.Sqrt(price / _maxPrice)));
    }

    private bool GetWallStuck(Entity<TransformComponent> ent)
    {
        if (ent.Comp.GridUid is not { } gridUid
            || ent.Comp.Anchored
            || ent.Comp.ParentUid != gridUid // ignore if not directly parented to grid
        )
            return false;

        var xfB = new Transform(ent.Comp.LocalPosition, 0);
        var shapeB = new PhysShapeCircle(0.001f);

        var contacts = _physics.GetContacts(ent.Owner);
        // it dies without this for some reason
        if (contacts == ContactEnumerator.Empty)
            return false;

        while (contacts.MoveNext(out var contact))
        {
            if (contact.FixtureA == null
                || contact.FixtureB == null
                || contact.BodyA == null
                || contact.BodyB == null
                || !contact.FixtureA.Hard
                || !contact.FixtureB.Hard
                || !contact.IsTouching
            )
                continue;

            var isA = contact.EntityB == ent.Owner;

            var body = isA ? contact.BodyA : contact.BodyB;
            // only trigger when the other entity is static
            if ((body.BodyType & BodyType.Static) == 0)
                continue;

            var fix = isA ? contact.FixtureA : contact.FixtureB;
            var xform = isA ? contact.XformA : contact.XformB;
            var anch = isA ? contact.EntityA : contact.EntityB;

            var xf = _physics.GetLocalPhysicsTransform(anch, xform);
            var shape = fix.Shape;

            if ((bool?)_testOverlap.Invoke(_manifold, [shape, 0, shapeB, 0, xf, xfB, false]) ?? false)
                return true;
        }

        return false;
    }

    public void QueueSweep(EntityCoordinates coordinates, TimeSpan delay, float radius, float aggression)
    {
        _sweepQueue.Enqueue(new Sweep(coordinates, radius, aggression), (_timing.CurTime + delay, _sweepSequence++));
    }

    public override void Update(float frameTime)
    {
        var start = Stopwatch.GetTimestamp();
        var deadline = start + WorkBudget;
        var periodicRemaining = MaxPeriodicCandidatesPerTick;
        UpdatePeriodicCleanup(frameTime, ref periodicRemaining, start + WorkBudget / 2);
        var periodicChecked = MaxPeriodicCandidatesPerTick - periodicRemaining;
        var sweepStart = Stopwatch.GetTimestamp();

        var remaining = MaxCandidatesPerTick - periodicChecked;
        var lookups = 0;
        var candidates = 0;
        var skippedQueued = 0;
        var queued = 0;
        while (remaining > 0 && Stopwatch.GetTimestamp() < deadline)
        {
            if (_remainingSweepCandidates == 0)
            {
                if (lookups >= MaxSweepLookupsPerTick || !_sweepQueue.TryPeek(out _, out var priority) ||
                    priority.Due > _timing.CurTime)
                    break;

                lookups++;
                BeginSweep(_sweepQueue.Dequeue());
                continue;
            }

            _sweepCandidates.MoveNext();
            var (uid, _) = _sweepCandidates.Current;
            _remainingSweepCandidates--;
            candidates++;
            remaining--;

            if (EntityManager.IsQueuedForDeletion(uid))
            {
                skippedQueued++;
                continue;
            }

            if (TerminatingOrDeleted(uid) || !_physQuery.HasComp(uid))
                continue;

            // Candidates may have moved since the lookup if this sweep was resumed on another tick.
            if (_sweepLookupTick != _timing.CurTick)
            {
                var transform = Transform(uid);
                if (transform.MapID != _sweepMap || !_sweepBounds.Intersects(_lookup.GetWorldAABB(uid, transform)))
                    continue;
            }

            if (ShouldEntityCleanup(uid, _activeAggression) && CleanupEnt(uid))
                queued++;
        }

        var end = Stopwatch.GetTimestamp();
        if ((end < deadline && remaining != 0 && lookups < MaxSweepLookupsPerTick) ||
            _nextTimingLog > _timing.RealTime)
            return;

        _nextTimingLog = _timing.RealTime + TimingLogInterval;
        Log.Warning(FormattableString.Invariant(
            $"Space cleanup tick: tick={_timing.CurTick.Value} elapsed_ms={Stopwatch.GetElapsedTime(start, end).TotalMilliseconds:F2} periodic_ms={Stopwatch.GetElapsedTime(start, sweepStart).TotalMilliseconds:F2} sweep_ms={Stopwatch.GetElapsedTime(sweepStart, end).TotalMilliseconds:F2} periodic_checked={periodicChecked} lookups={lookups} candidates={candidates} skipped_queued={skippedQueued} queued={queued} pending_sweeps={_sweepQueue.Count} pending_candidates={_remainingSweepCandidates}"));
    }

    private void BeginSweep(Sweep sweep)
    {
        if (!sweep.Coordinates.IsValid(EntityManager))
            return;

        var center = _transform.ToMapCoordinates(sweep.Coordinates);
        _sweepMap = center.MapId;
        _sweepBounds = Box2.CenteredAround(center.Position, new Vector2(sweep.Radius * 2));
        _sweepLookupTick = _timing.CurTick;
        _activeAggression = sweep.Aggression;
        _sweepEnts.Clear();
        _lookup.GetEntitiesInRange(center, sweep.Radius, _sweepEnts,
            LookupFlags.Dynamic | LookupFlags.Approximate | LookupFlags.Sundries);
        _sweepCandidates = _sweepEnts.GetEnumerator();
        _remainingSweepCandidates = _sweepEnts.Count;
    }

    private void OnRoundCleanup(RoundRestartCleanupEvent _)
    {
        _sweepQueue.Clear();
        _sweepCandidates = default;
        _sweepEnts.Clear();
        _remainingSweepCandidates = 0;
        _sweepSequence = 0;
        _nextTimingLog = TimeSpan.Zero;
    }
}

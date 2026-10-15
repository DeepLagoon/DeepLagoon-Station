using Content.Shared.Damage;
using Content.Shared.GameTicking;
using Content.Shared.Spreader;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.Spreader;

public sealed partial class KudzuSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IRobustRandom _robustRandom = default!;
    private SharedMapSystem _map => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private DamageableSystem _damageable = default!;

    [ValidatePrototypeId<EdgeSpreaderPrototype>]
    private const string KudzuGroup = "Kudzu";

    private static readonly TimeSpan GrowthTickInterval = TimeSpan.FromSeconds(0.5);
    private readonly PriorityQueue<GrowthScheduleEntry, TimeSpan> _growthQueue = new();
    private EntityQuery<GrowingKudzuComponent> _growingQuery;
    private EntityQuery<KudzuComponent> _kudzuQuery;
    private EntityQuery<DamageableComponent> _damageableQuery;
    private EntityQuery<AppearanceComponent> _appearanceQuery;

    // The component reference distinguishes removal/re-addition on the same entity.
    private readonly record struct GrowthScheduleEntry(EntityUid Uid, GrowingKudzuComponent Component);

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();
        _growingQuery = GetEntityQuery<GrowingKudzuComponent>();
        _kudzuQuery = GetEntityQuery<KudzuComponent>();
        _damageableQuery = GetEntityQuery<DamageableComponent>();
        _appearanceQuery = GetEntityQuery<AppearanceComponent>();
        SubscribeLocalEvent<KudzuComponent, ComponentStartup>(SetupKudzu);
        SubscribeLocalEvent<KudzuComponent, SpreadNeighborsEvent>(OnKudzuSpread);
        SubscribeLocalEvent<KudzuComponent, DamageChangedEvent>(OnDamageChanged);
        SubscribeLocalEvent<GrowingKudzuComponent, ComponentStartup>(OnGrowingStartup);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
    }

    private void OnGrowingStartup(Entity<GrowingKudzuComponent> ent, ref ComponentStartup args)
    {
        _growthQueue.Enqueue(new GrowthScheduleEntry(ent.Owner, ent.Comp), ent.Comp.NextTick);
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent args)
    {
        _growthQueue.Clear();
    }

    private void OnDamageChanged(EntityUid uid, KudzuComponent component, DamageChangedEvent args)
    {
        // Every time we take any damage, we reduce growth depending on all damage over the growth impact
        //   So the kudzu gets slower growing the more it is hurt.
        var growthDamage = (int) (args.Damageable.TotalDamage / component.GrowthHealth);
        if (growthDamage > 0)
        {
            if (!EnsureComp<GrowingKudzuComponent>(uid, out _))
                component.GrowthLevel = 3;

            component.GrowthLevel = Math.Max(1, component.GrowthLevel - growthDamage);
            if (EntityManager.TryGetComponent<AppearanceComponent>(uid, out var appearance))
            {
                _appearance.SetData(uid, KudzuVisuals.GrowthLevel, component.GrowthLevel, appearance);
            }
        }
    }

    private void OnKudzuSpread(EntityUid uid, KudzuComponent component, ref SpreadNeighborsEvent args)
    {
        if (component.GrowthLevel < 3)
            return;

        if (args.NeighborFreeTiles.Count == 0)
        {
            RemCompDeferred<ActiveEdgeSpreaderComponent>(uid);
            return;
        }

        if (!_robustRandom.Prob(component.SpreadChance))
            return;

        var prototype = MetaData(uid).EntityPrototype?.ID;

        if (prototype == null)
        {
            RemCompDeferred<ActiveEdgeSpreaderComponent>(uid);
            return;
        }

        foreach (var neighbor in args.NeighborFreeTiles)
        {
            var neighborUid = Spawn(prototype, _map.GridTileToLocal(neighbor.Tile.GridUid, neighbor.Grid, neighbor.Tile.GridIndices));
            DebugTools.Assert(HasComp<EdgeSpreaderComponent>(neighborUid));
            DebugTools.Assert(HasComp<ActiveEdgeSpreaderComponent>(neighborUid));
            DebugTools.Assert(Comp<EdgeSpreaderComponent>(neighborUid).Id == KudzuGroup);
            args.Updates--;
            if (args.Updates <= 0)
                return;
        }
    }

    private void SetupKudzu(EntityUid uid, KudzuComponent component, ComponentStartup args)
    {
        if (!EntityManager.TryGetComponent<AppearanceComponent>(uid, out var appearance))
        {
            return;
        }

        _appearance.SetData(uid, KudzuVisuals.Variant, _robustRandom.Next(1, component.SpriteVariants), appearance);
        _appearance.SetData(uid, KudzuVisuals.GrowthLevel, 1, appearance);
    }

    /// <inheritdoc/>
    public override void Update(float frameTime)
    {
        var curTime = _timing.CurTime;

        while (_growthQueue.TryPeek(out var entry, out var dueTime) && dueTime <= curTime)
        {
            _growthQueue.Dequeue();
            var uid = entry.Uid;
            if (TerminatingOrDeleted(uid) || !_growingQuery.TryGetComponent(uid, out var grow) ||
                !ReferenceEquals(entry.Component, grow) || !grow.Running)
                continue;

            // AutoPausedField can move NextTick after this entry was queued.
            if (Paused(uid) || grow.NextTick > curTime)
            {
                _growthQueue.Enqueue(entry, Paused(uid) ? curTime + GrowthTickInterval : grow.NextTick);
                continue;
            }

            grow.NextTick = curTime + GrowthTickInterval;

            if (!_kudzuQuery.TryGetComponent(uid, out var kudzu))
            {
                RemCompDeferred(uid, grow);
                continue;
            }

            if (!_robustRandom.Prob(kudzu.GrowthTickChance))
            {
                _growthQueue.Enqueue(entry, grow.NextTick);
                continue;
            }

            if (_damageableQuery.TryGetComponent(uid, out var damage))
            {
                if (damage.TotalDamage > 1.0)
                {
                    if (kudzu.DamageRecovery != null)
                    {
                        // This kudzu features healing, so Gradually heal
                        _damageable.TryChangeDamage(uid, kudzu.DamageRecovery, true);
                    }
                    if (damage.TotalDamage >= kudzu.GrowthBlock)
                    {
                        // Don't grow when quite damaged
                        if (_robustRandom.Prob(0.95f))
                        {
                            _growthQueue.Enqueue(entry, grow.NextTick);
                            continue;
                        }
                    }
                }
            }

            kudzu.GrowthLevel += 1;

            if (kudzu.GrowthLevel >= 3)
            {
                // why cache when you can simply cease to be? Also saves a bit of memory/time.
                RemCompDeferred(uid, grow);
            }
            else
            {
                _growthQueue.Enqueue(entry, grow.NextTick);
            }

            if (_appearanceQuery.TryGetComponent(uid, out var appearance))
            {
                _appearance.SetData(uid, KudzuVisuals.GrowthLevel, kudzu.GrowthLevel, appearance);
            }
        }
    }
}

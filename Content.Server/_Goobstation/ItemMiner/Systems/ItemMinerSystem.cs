using System.Diagnostics;
using Content.Shared._Goobstation.ItemMiner;
using Content.Server.Power.EntitySystems;
using Content.Server.Spawners.EntitySystems;
using Content.Server.Stack;
using Content.Shared.Stacks;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server._Goobstation.ItemMiner;

public sealed partial class ItemMinerSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IRobustRandom _gambling = default!;
    [Dependency] private PowerReceiverSystem _power = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private StackSystem _stack = default!;
    [Dependency] private ConditionalSpawnerSystem _spawners = default!;

    private const int TimerVersion = 1;
    private const int MaxAttemptsPerTick = 8;
    private const int MaxQueueChecksPerTick = 64;
    private static readonly long SpawnBudget = Math.Max(1L, Stopwatch.Frequency / 500); // 2 ms, at least one attempt.
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);
    private readonly record struct ReadyMiner(ItemMinerComponent Component, EntProtoId Prototype);
    // Reserve a typical drill cohort once; later ticks reuse these buffers.
    private readonly Queue<EntityUid> _pending = new(MaxQueueChecksPerTick);
    private readonly HashSet<EntityUid> _queued = new(MaxQueueChecksPerTick);
    private readonly Dictionary<EntityUid, ReadyMiner> _ready = new(MaxQueueChecksPerTick);
    private readonly HashSet<Entity<StackComponent>> _mergeContacts = new(MaxQueueChecksPerTick);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ItemMinerComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<ItemMinerComponent, MapInitEvent>(OnInit);
        InitializeDiagnostics();
    }

    public override void Shutdown()
    {
        _pending.Clear();
        _queued.Clear();
        _ready.Clear();
        _mergeContacts.Clear();
        base.Shutdown();
    }

    private TimeSpan Interval(ItemMinerComponent miner)
    {
        if (miner.Interval > TimeSpan.Zero) return miner.Interval;
        miner.Interval = DefaultInterval;
        _invalidIntervals++;
        return miner.Interval;
    }

    private static TimeSpan AddInterval(TimeSpan time, TimeSpan interval)
        => interval > TimeSpan.MaxValue - time ? TimeSpan.MaxValue : time + interval;

    private void OnStartup(Entity<ItemMinerComponent> ent, ref ComponentStartup args)
    {
        var now = _timing.CurTime;
        var limit = AddInterval(now, Interval(ent.Comp));
        if (ent.Comp.TimerVersion != TimerVersion)
        {
            // Old snapshots contain absolute deadlines. They have already been read as offsets by the new
            // serializer, so neither their apparent backlog nor their apparent many-hour delay is trustworthy.
            ent.Comp.NextAt = limit;
            ent.Comp.TimerVersion = TimerVersion;
            ent.Comp.PendingPayouts = 0;
            _timerResets++;
        }
        else if (ent.Comp.NextAt < now || ent.Comp.NextAt > limit)
        {
            // An expired offset contributes at most one current cycle, never elapsed offline intervals.
            // Payouts already earned before the save are stored separately.
            ent.Comp.NextAt = ent.Comp.NextAt < now ? now : limit;
            _timerResets++;
        }
        if (ent.Comp.PendingPayouts < 0) ent.Comp.PendingPayouts = 0;
    }

    private void OnInit(Entity<ItemMinerComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.TimerVersion = TimerVersion;
        ent.Comp.PendingPayouts = 0;
        ent.Comp.NextAt = AddInterval(_timing.CurTime, Interval(ent.Comp));
    }

    public override void Update(float frameTime)
    {
        var measure = _miningDiagnosticsEnabled;
        var started = measure ? Stopwatch.GetTimestamp() : 0;
        _frame = default;
        _ready.Clear();
        var now = _timing.CurTime;
        var disabledTime = TimeSpan.FromSeconds(frameTime);
        var query = EntityQueryEnumerator<ItemMinerComponent>();
        while (query.MoveNext(out var uid, out var miner))
        {
            if (TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid)) continue;
            _frame.Miners++;
            var checkEv = new ItemMinerCheckEvent(miner.Proto);
            RaiseLocalEvent(uid, ref checkEv);
            if (TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid)
                || !TryComp<TransformComponent>(uid, out var xform)) continue;
            if (checkEv.Cancelled || miner.NeedApcPower && !_power.IsPowered(uid)
                || miner.NeedsAnchored && !xform.Anchored)
            {
                // Freeze the remaining interval while disabled, including an already queued payout.
                miner.NextAt = AddInterval(miner.NextAt, disabledTime);
                if (miner.AudioUid != null)
                {
                    QueueDel(miner.AudioUid);
                    miner.AudioUid = null;
                }
                if (miner.NeedApcPower) _power.SetPowerDisabled(uid, checkEv.Cancelled);
                _frame.PendingPayouts += miner.PendingPayouts;
                continue;
            }
            _frame.Active++;
            if (miner.NeedApcPower) _power.SetPowerDisabled(uid, false);
            if (TerminatingOrDeleted(miner.AudioUid) && miner.MiningSound != null)
                miner.AudioUid = _audio.PlayPvs(miner.MiningSound, uid)?.Entity;
            if (miner.NextAt <= now)
            {
                _frame.Due++;
                _frame.MaxLateness = Math.Max(_frame.MaxLateness, (now - miner.NextAt).TotalSeconds);
                var interval = Interval(miner);
                var next = AddInterval(miner.NextAt, interval);
                if (next <= now)
                {
                    // An old/invalid deadline never produces a historical batch. Earn just this cycle.
                    _missedIntervals += (now - next).Ticks / interval.Ticks + 1;
                    next = AddInterval(now, interval);
                }
                // Account for active production on its original schedule, independently of queue delays.
                miner.NextAt = next;
                if (miner.PendingPayouts < long.MaxValue) miner.PendingPayouts++;
            }
            _frame.PendingPayouts += miner.PendingPayouts;
            if (miner.PendingPayouts <= 0) continue;
            _ready[uid] = new ReadyMiner(miner, checkEv.Proto);
            if (_queued.Add(uid)) _pending.Enqueue(uid);
        }
        // Queue budget clocks remain active even with diagnostics off.
        var scanned = measure || _pending.Count > 0 ? Stopwatch.GetTimestamp() : 0;
        if (measure) _frame.CheckTicks = scanned - started;
        if (_pending.Count > 0) ProcessPending(scanned, measure);
        _frame.Deferred = _pending.Count;
        if (measure)
        {
            var finished = Stopwatch.GetTimestamp();
            _frame.TotalTicks = finished - started;
            RecordDiagnostics(finished);
        }
    }

    private void ProcessPending(long started, bool measure)
    {
        var checks = Math.Min(_pending.Count, MaxQueueChecksPerTick);
        while (checks-- > 0 && _frame.Attempts < MaxAttemptsPerTick)
        {
            if (_frame.Attempts > 0 && Stopwatch.GetTimestamp() - started >= SpawnBudget) break;
            var uid = _pending.Dequeue();
            _queued.Remove(uid);
            // _ready contains only unpaused miners that passed this tick's power/planet/anchoring checks.
            if (!_ready.TryGetValue(uid, out var ready) || TerminatingOrDeleted(uid)
                || EntityManager.IsQueuedForDeletion(uid)
                || !TryComp<ItemMinerComponent>(uid, out var miner) || !ReferenceEquals(miner, ready.Component)
                || !TryComp<MetaDataComponent>(uid, out var metadata) || metadata.EntityPaused
                || miner.PendingPayouts <= 0) continue;

            miner.PendingPayouts--;
            _frame.PendingPayouts--;
            if (miner.PendingPayouts > 0)
            {
                // Keep already-earned work, but give other miners their turn before paying it out.
                _queued.Add(uid);
                _pending.Enqueue(uid);
            }
            _frame.Attempts++;
            if (miner.SpawnChance < 1f && !_gambling.Prob(miner.SpawnChance)) continue;

            var produce = new ItemMinerProduceEvent();
            RaiseLocalEvent(uid, ref produce);
            if (produce.Handled) continue;

            var spawnStart = measure ? Stopwatch.GetTimestamp() : 0;
            var coords = Transform(uid).Coordinates;
            EntityUid? mined;
            if (!miner.ResolveRandomSpawner || !_spawners.TrySpawnRandom(uid, ready.Prototype, coords, out mined))
                mined = Spawn(ready.Prototype, coords);
            if (measure) _frame.SpawnTicks += Stopwatch.GetTimestamp() - spawnStart;
            if (mined is not { } minedUid) continue; // Empty random-spawner roll.
            _frame.Spawned++;
            var ev = new ItemMinedEvent(minedUid);
            var eventStart = measure ? Stopwatch.GetTimestamp() : 0;
            RaiseLocalEvent(uid, ref ev);
            if (measure) _frame.EffectTicks += Stopwatch.GetTimestamp() - eventStart;
            if (!ev.NoStack && !TerminatingOrDeleted(minedUid) && !EntityManager.IsQueuedForDeletion(minedUid))
            {
                var mergeStart = measure ? Stopwatch.GetTimestamp() : 0;
                if (_stack.TryMergeToContacts(minedUid, null, null, _mergeContacts)) _frame.Merged++;
                if (measure) _frame.MergeTicks += Stopwatch.GetTimestamp() - mergeStart;
            }
            if (miner.MinedSound != null)
            {
                var soundStart = measure ? Stopwatch.GetTimestamp() : 0;
                _audio.PlayPvs(miner.MinedSound, uid);
                if (measure) _frame.EffectTicks += Stopwatch.GetTimestamp() - soundStart;
            }
        }
    }
}

using Content.Shared._Mono.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server._Mono.Cleanup;

public abstract partial class BaseCleanupSystem<TComp> : EntitySystem
    where TComp : IComponent
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    protected TimeSpan _cleanupInterval = TimeSpan.FromSeconds(300);
    protected TimeSpan _debugCleanupInterval = TimeSpan.FromSeconds(15);
    protected bool _doDebug;
    protected bool _doLog;

    private readonly LinkedList<EntityUid> _candidates = new();
    private readonly Dictionary<EntityUid, LinkedListNode<EntityUid>> _candidateNodes = new();
    private LinkedListNode<EntityUid>? _nextCandidate;
    private LinkedListNode<EntityUid>? _lastCandidate;

    private TimeSpan _nextCleanup = TimeSpan.Zero;
    private int _delCount = 0;
    // used to track when we should be cleaning up the next entry in our queue
    private TimeSpan _cleanupAccumulator = TimeSpan.Zero;
    private TimeSpan _cleanupDeferDuration;

    public override void Initialize()
    {
        base.Initialize();

        Subs.CVar(_cfg, MonoCVars.CleanupDebug, val => _doDebug = val, true);
        Subs.CVar(_cfg, MonoCVars.CleanupLog, val => _doLog = val, true);
        EntityManager.ComponentAdded += OnCandidateAdded;
        EntityManager.ComponentRemoved += OnCandidateRemoved;
    }

    public override void Shutdown()
    {
        EntityManager.ComponentAdded -= OnCandidateAdded;
        EntityManager.ComponentRemoved -= OnCandidateRemoved;
        base.Shutdown();
    }

    private void OnCandidateAdded(AddedComponentEventArgs args)
    {
        if (args.BaseArgs.Component is TComp)
            _candidateNodes.Add(args.BaseArgs.Owner, _candidates.AddLast(args.BaseArgs.Owner));
    }

    private void OnCandidateRemoved(RemovedComponentEventArgs args)
    {
        if (args.BaseArgs.Component is not TComp || !_candidateNodes.Remove(args.BaseArgs.Owner, out var node))
            return;

        if (node == _nextCandidate)
            _nextCandidate = node == _lastCandidate ? null : node.Next;

        if (node == _lastCandidate)
            _lastCandidate = _nextCandidate == null ? null : node.Previous;

        _candidates.Remove(node);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var remaining = int.MaxValue;
        UpdatePeriodicCleanup(frameTime, ref remaining, long.MaxValue);
    }

    protected void UpdatePeriodicCleanup(float frameTime, ref int remaining, long deadline)
    {
        if (_nextCandidate != null)
        {
            _cleanupAccumulator += TimeSpan.FromSeconds(frameTime);
            while (remaining > 0 && _nextCandidate is { } candidate && _cleanupAccumulator > _cleanupDeferDuration &&
                   (deadline == long.MaxValue || Stopwatch.GetTimestamp() < deadline))
            {
                remaining--;
                _cleanupAccumulator -= _cleanupDeferDuration;

                _nextCandidate = candidate == _lastCandidate ? null : candidate.Next;
                if (_nextCandidate == null)
                    _lastCandidate = null;

                var uid = candidate.Value;
                if (!TryComp<MetaDataComponent>(uid, out var metadata) || metadata.EntityPaused ||
                    metadata.EntityLifeStage >= EntityLifeStage.Terminating || EntityManager.IsQueuedForDeletion(uid))
                    continue;

                if (!ShouldEntityCleanup(uid))
                    continue;

                CleanupEnt(uid);
            }
            return;
        }

        if (_delCount != 0)
        {
            Log.Info($"Deleted {_delCount} entities");
            _delCount = 0;
        }

        // we appear to be done with previous queue so try get another
        var curTime = _timing.CurTime;
        if (curTime < _nextCleanup)
            return;
        var interval = !_doDebug ? _cleanupInterval : _debugCleanupInterval;
        _nextCleanup = curTime + interval;

        // Bound this pass to the current tail. Entities created during a pass are visited next time.
        _nextCandidate = _candidates.First;
        _lastCandidate = _candidates.Last;
        _cleanupAccumulator = TimeSpan.Zero;
        if (_candidates.Count != 0)
            _cleanupDeferDuration = interval * 0.9 / _candidates.Count;

        Log.Debug($"Started cleanup pass over {_candidates.Count} candidates, check interval {_cleanupDeferDuration}");
    }

    protected bool CleanupEnt(EntityUid uid)
    {
        if (!EntityManager.TryQueueDeleteEntity(uid))
            return false;

        if (_doLog)
        {
            var coord = Transform(uid).Coordinates;
            var world = _transform.ToMapCoordinates(coord);
            Log.Info($"Cleanup deleting entity {ToPrettyString(uid)} at {coord} (world {world})");
        }

        _delCount += 1;
        return true;
    }

    protected abstract bool ShouldEntityCleanup(EntityUid uid);
}

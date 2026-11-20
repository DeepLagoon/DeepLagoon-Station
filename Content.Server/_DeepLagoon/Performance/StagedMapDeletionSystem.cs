using System.Diagnostics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._DeepLagoon.Performance;

/// <summary>
/// Retires unloaded maps/grids a branch at a time, using normal entity deletion for each branch.
/// Containers and mobs remain atomic subtrees; no entity/component teardown runs on a worker.
/// </summary>
public sealed class StagedMapDeletionSystem : EntitySystem
{
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    private sealed class Removal(EntityUid root)
    {
        public readonly EntityUid Root = root;
        public readonly Stack<EntityUid> Parents = new([root]);
        public long Branches;
        public double MaximumMs;
        public readonly long Started = System.Diagnostics.Stopwatch.GetTimestamp();
    }
    private readonly Queue<Removal> _pending = new();
    private readonly HashSet<EntityUid> _roots = new();
    public int PendingCount => _pending.Count;
    public bool IsPending(EntityUid root) => _roots.Contains(root);

    /// <summary>
    /// Whether spawning at valid coordinates would put an entity on a retiring map or grid.
    /// Map-relative coordinates also need a spatial check: spawning can attach to a grid at that point.
    /// </summary>
    public bool IsPendingAt(EntityCoordinates coordinates)
    {
        if (_roots.Count == 0)
            return false;

        if (IsPending(coordinates.EntityId)
            || (_transform.GetMap(coordinates) is { } map && IsPending(map))
            || (_transform.GetGrid(coordinates) is { } grid && IsPending(grid)))
            return true;

        return _maps.TryFindGridAt(_transform.ToMapCoordinates(coordinates), out var spatialGrid, out _)
            && IsPending(spatialGrid);
    }

    public void Queue(EntityUid root)
    {
        if (!Exists(root) || EntityManager.IsQueuedForDeletion(root) || !_roots.Add(root)) return;
        if (!HasComp<MapComponent>(root) && !HasComp<MapGridComponent>(root))
        { _roots.Remove(root); QueueDel(root); return; }
        // A retired expedition no longer simulates or admits visitors while its branches are removed.
        if (TryComp<MapComponent>(root, out var map)) _maps.SetPaused(map.MapId, true);
        _pending.Enqueue(new Removal(root));
    }

    public override void Update(float frameTime)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var branches = 0;
        while (_pending.TryPeek(out var removal) && branches < 128 &&
               System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < 4)
        {
            if (removal.Parents.Count == 0 || !Exists(removal.Root))
            {
                _pending.Dequeue(); _roots.Remove(removal.Root);
                if (removal.Branches > 128 || removal.MaximumMs >= 10)
                    Log.Info($"Staged map deletion complete: root={removal.Root} branches={removal.Branches} max_branch_ms={removal.MaximumMs:F2} wall_ms={System.Diagnostics.Stopwatch.GetElapsedTime(removal.Started).TotalMilliseconds:F1}");
                continue;
            }
            var parent = removal.Parents.Peek();
            if (!TryComp<TransformComponent>(parent, out var transform)) { removal.Parents.Pop(); continue; }
            var children = transform.ChildEnumerator;
            if (children.MoveNext(out var child))
            {
                if (HasComp<MapGridComponent>(child)) { removal.Parents.Push(child); continue; }
            }
            else { child = parent; removal.Parents.Pop(); }

            var before = System.Diagnostics.Stopwatch.GetTimestamp();
            Del(child);
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(before).TotalMilliseconds;
            removal.MaximumMs = Math.Max(removal.MaximumMs, elapsed);
            removal.Branches++; branches++;
            if (elapsed >= 33.33) Log.Warning($"Slow map deletion branch: root={removal.Root} entity={child} elapsed_ms={elapsed:F2}");
        }
    }

    public override void Shutdown()
    {
        // Shutdown has no tick budget; normal recursive deletion completes any outstanding requests.
        foreach (var removal in _pending) if (Exists(removal.Root)) Del(removal.Root);
        _pending.Clear(); _roots.Clear();
        base.Shutdown();
    }
}

using System.Numerics;
using Content.Server._DeepLagoon.Performance;
using Content.Server.StationEvents.Components;
using Robust.Shared.Map;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server._NF.StationEvents.Events;

public sealed partial class BluespaceErrorRule
{
    private sealed class GridRetirement
    {
        public readonly List<Entity<TransformComponent>> Grids = new();
        public readonly List<EntityUid> OriginalParents = new();
        public readonly List<(Entity<TransformComponent> Entity, EntityUid MapUid, Vector2 MapPosition)> Players = new();
        public EntityUid Map;
        public long Start, MapCreated, Paused, Moved, LifecycleReleased, Queued;
    }

    private bool IsGridRetiring(TransformComponent transform) => transform.MapUid is { } map &&
        HasComp<BluespaceRetirementMapComponent>(map);

    private GridRetirement PrepareGridRetirement(EntityUid root)
    {
        var retirement = new GridRetirement();
        var pending = new Stack<EntityUid>();
        var visited = new HashSet<EntityUid>();
        pending.Push(root);
        while (pending.TryPop(out var uid))
        {
            if (!visited.Add(uid) || TerminatingOrDeleted(uid) ||
                !TryComp<TransformComponent>(uid, out var transform) || IsGridRetiring(transform))
                continue;

            retirement.Grids.Add((uid, transform));
            retirement.OriginalParents.Add(transform.ParentUid);
            if (TryComp<LinkedLifecycleGridParentComponent>(uid, out var parent))
            {
                foreach (var linked in parent.LinkedEntities)
                    pending.Push(linked);
            }
        }

        // Native linked-player discovery is per grid, not recursive. Include split
        // fragments and deduplicate vehicles/pulled roots before any grid changes map.
        var protectedRoots = new HashSet<EntityUid>();
        foreach (var grid in retirement.Grids)
        {
            foreach (var player in _linkedLifecycleGrid.GetEntitiesToReparent(grid.Owner))
            {
                if (!TerminatingOrDeleted(player.Entity.Owner) && protectedRoots.Add(player.Entity.Owner))
                    retirement.Players.Add(player);
            }
        }
        foreach (var player in retirement.Players)
            _transform.DetachEntity(player.Entity.Owner, player.Entity.Comp);

        return retirement;
    }

    private void RetirePublishedGrid(GridRetirement retirement, bool measure)
    {
        retirement.Start = measure ? Stopwatch.GetTimestamp() : 0;
        // An uninitialized, explicitly paused private map cannot admit normal visitors
        // or simulate the retired bodies while their native teardown is sliced.
        retirement.Map = _map.CreateMap(out var mapId, runMapInit: false);
        try
        {
            EnsureComp<BluespaceRetirementMapComponent>(retirement.Map);
            retirement.MapCreated = measure ? Stopwatch.GetTimestamp() : 0;
            _map.SetPaused(mapId, true);
            retirement.Paused = measure ? Stopwatch.GetTimestamp() : 0;
            foreach (var grid in retirement.Grids)
                _transform.SetParent(grid.Owner, grid.Comp, retirement.Map);
            retirement.Moved = measure ? Stopwatch.GetTimestamp() : 0;

            // Every linked root is now owned by this private map. Removing the
            // lifecycle parent before Terminating prevents its native OnRemove from
            // turning the final staged branch back into recursive linked-grid Del.
            foreach (var grid in retirement.Grids)
                RemComp<LinkedLifecycleGridParentComponent>(grid.Owner);
            retirement.LifecycleReleased = measure ? Stopwatch.GetTimestamp() : 0;
        }
        catch
        {
            // If a map-change handler fails partway through isolation, don't let a
            // queued private root's lifecycle cascade destroy the still-public family.
            for (var i = retirement.Grids.Count - 1; i >= 0; i--)
            {
                var grid = retirement.Grids[i];
                if (TerminatingOrDeleted(grid.Owner) || grid.Comp.MapUid != retirement.Map)
                    continue;
                try
                {
                    var original = retirement.OriginalParents[i];
                    if (!TerminatingOrDeleted(original))
                        _transform.SetParent(grid.Owner, grid.Comp, original);
                }
                catch (Exception error)
                {
                    Log.Error($"Unable to restore bluespace grid {grid.Owner} after failed retirement: {error}");
                }
            }
            foreach (var grid in retirement.Grids)
            {
                if (!TerminatingOrDeleted(grid.Owner) && grid.Comp.MapUid == retirement.Map)
                    RemComp<LinkedLifecycleGridParentComponent>(grid.Owner);
            }
            throw;
        }
        finally
        {
            // Ownership survives rule removal and partially completed retirement.
            EntityManager.System<StagedMapDeletionSystem>().Queue(retirement.Map);
            retirement.Queued = measure ? Stopwatch.GetTimestamp() : 0;
        }
    }

    private void RestoreRetirementPlayers(GridRetirement retirement)
    {
        foreach (var player in retirement.Players)
        {
            if (TerminatingOrDeleted(player.Entity.Owner))
                continue;
            // Match native linked-grid preservation: another handler may already
            // have rescued this root from nullspace; don't overwrite that move.
            if (player.Entity.Comp.MapID == MapId.Nullspace)
                _transform.SetCoordinates(player.Entity.Owner, new EntityCoordinates(player.MapUid, player.MapPosition));
        }
    }
}

/// <summary>Transient ownership of an expired, unpublished Bluespace grid family.</summary>
[RegisterComponent]
public sealed partial class BluespaceRetirementMapComponent : Component;

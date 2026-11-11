using System.Numerics;
using Content.Server.Atmos.Components;
using Content.Shared._DeepLagoon.GridMagnet;
using Content.Server._DeepLagoon.Power;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Database;
using Content.Shared.Decals;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.GridMagnet;

public sealed partial class GridMagnetSystem
{
    [Dependency] private PoiPowerRestrictionSystem _poiPowerRestriction = default!;

    /// <summary>
    /// Gas from the merged-away grid that is waiting for the kept grid's atmosphere to create its tiles.
    /// </summary>
    private sealed class PendingGas
    {
        public EntityUid Grid;
        public List<(Vector2i Index, GasMixture Air)> Tiles = new();
        public TimeSpan Deadline;
    }

    /// <summary>
    /// How long to wait for the kept grid's atmosphere to revalidate the new tiles before dropping the gas.
    /// </summary>
    private static readonly TimeSpan GasRestoreTimeout = TimeSpan.FromSeconds(20);

    private readonly List<PendingGas> _pendingGas = new();

    private void Weld(Pair pair)
    {
        // Either way the machines are done: they go idle and the pair is gone before anything is changed.
        EndPair(pair, deactivate: true, null);

        var plan = pair.Plan;

        // Tiles may have been built on the kept grid since the plan was made.
        var overlap = CountOverlap(plan);
        if (overlap > 0)
        {
            NotifyBoth(pair, "dl-grid-magnet-failed",
                ("reason", Loc.GetString("dl-grid-magnet-fail-overlap", ("count", overlap))));
            return;
        }

        if (!ExecuteMerge(plan))
        {
            NotifyBoth(pair, "dl-grid-magnet-failed", ("reason", Loc.GetString("dl-grid-magnet-fail-generic")));
            return;
        }

        NotifyBoth(pair, "dl-grid-magnet-merged");
    }

    /// <summary>
    /// Merges the pulled grid into the kept grid with the engine's <see cref="GridFixtureSystem.Merge"/>.
    /// The engine moves the tiles and the anchored entities and deletes the pulled grid. What it leaves undone
    /// is done here:
    /// <list type="bullet">
    /// <item>It re-anchors entities without raising <c>EntParentChangedMessage</c>, so systems that register
    /// entities with their grid on a parent change (atmos devices, consoles, gravity, fire control) never see
    /// the move. The message is raised for them afterwards.</item>
    /// <item>The pulled grid's gas, decals and per-grid components are deleted with it. Gas and decals are
    /// copied over; pipe gas travels with the pipes.</item>
    /// <item>Entities parented to the grid but outside its tiles would trip an engine assert and be deleted, so
    /// they are moved off the grid first.</item>
    /// </list>
    /// Pipe and cable networks need nothing extra: re-anchoring queues a reflood of every node, which also
    /// joins them to the nodes already on the kept grid next to the seam.
    /// </summary>
    private bool ExecuteMerge(Plan plan)
    {
        if (!TryComp(plan.KeeperGrid, out MapGridComponent? keeperGrid) ||
            !TryComp(plan.PulledGrid, out MapGridComponent? pulledGrid))
        {
            return false;
        }

        var keeperXform = Transform(plan.KeeperGrid);
        var pulledXform = Transform(plan.PulledGrid);
        var mapUid = pulledXform.MapUid;

        // Taken now: the pulled grid no longer exists afterwards.
        var pulledName = ToPrettyString(plan.PulledGrid);
        var keeperName = ToPrettyString(plan.KeeperGrid);

        EvacuateStrays(plan.PulledGrid, pulledGrid, pulledXform);

        var gas = SnapshotGas(plan);
        var decals = SnapshotDecals(plan.PulledGrid);
        var anchored = SnapshotAnchored(pulledXform);
        TryComp<PoiPowerRestrictionComponent>(plan.PulledGrid, out var pulledPowerRestriction);

        try
        {
            _gridFixture.Merge(plan.KeeperGrid, plan.PulledGrid, plan.Offset, Angle.FromDegrees(90 * plan.Turns),
                keeperGrid, pulledGrid, keeperXform, pulledXform);
        }
        catch (Exception e)
        {
            // The engine merge is not transactional; if it throws, the grids may be half merged.
            Log.Error($"Grid magnet merge of {pulledName} into {keeperName} failed: {e}");
            return false;
        }

        _poiPowerRestriction.TransferAfterMerge(plan.KeeperGrid, pulledPowerRestriction);

        // Entities that went with the tiles have the kept grid as parent now. Tell the systems that wait for it.
        foreach (var uid in anchored)
        {
            if (TerminatingOrDeleted(uid) || !_xformQuery.TryGetComponent(uid, out var xform) || xform.GridUid != plan.KeeperGrid)
                continue;

            var ev = new EntParentChangedMessage(uid, plan.PulledGrid, mapUid, xform);
            RaiseLocalEvent(uid, ref ev, true);
        }

        RestoreDecals(plan, decals);

        if (gas.Count > 0)
        {
            _pendingGas.Add(new PendingGas
            {
                Grid = plan.KeeperGrid,
                Tiles = gas,
                Deadline = _timing.CurTime + GasRestoreTimeout,
            });
        }

        _adminLog.Add(LogType.Action, LogImpact.High,
            $"Grid magnet merged grid {pulledName:entity} into {keeperName:entity}");
        return true;
    }

    /// <summary>
    /// The engine merge only carries entities that stand inside a tile of the grid. Anything else parented to the
    /// grid is moved to the map or a neighbouring grid at its current world position.
    /// </summary>
    private void EvacuateStrays(EntityUid gridUid, MapGridComponent grid, TransformComponent xform)
    {
        var strays = new List<EntityUid>();
        var enumerator = xform.ChildEnumerator;
        while (enumerator.MoveNext(out var child))
        {
            var childXform = Transform(child);
            var tile = _map.CoordinatesToTile(gridUid, grid, childXform.Coordinates);
            if (_map.TryGetTile(grid, tile, out var existing) && !existing.IsEmpty)
                continue;

            strays.Add(child);
        }

        foreach (var stray in strays)
        {
            var strayXform = Transform(stray);
            if (strayXform.Anchored)
                _transform.Unanchor(stray, strayXform);

            _transform.AttachToGridOrMap(stray, strayXform);
        }
    }

    private List<EntityUid> SnapshotAnchored(TransformComponent pulledXform)
    {
        var anchored = new List<EntityUid>();
        var enumerator = pulledXform.ChildEnumerator;
        while (enumerator.MoveNext(out var child))
        {
            if (_xformQuery.TryGetComponent(child, out var xform) && xform.Anchored)
                anchored.Add(child);
        }

        return anchored;
    }

    /// <summary>
    /// Copies the breathable gas of every real tile of the pulled grid, keyed by the tile it moves to.
    /// </summary>
    private List<(Vector2i Index, GasMixture Air)> SnapshotGas(Plan plan)
    {
        var result = new List<(Vector2i, GasMixture)>();
        if (!TryComp(plan.PulledGrid, out GridAtmosphereComponent? atmos))
            return result;

        foreach (var (index, tile) in atmos.Tiles)
        {
            if (tile.NoGridTile || tile.MapAtmosphere || tile.Air is not { Immutable: false } air || air.TotalMoles <= 0f)
                continue;

            result.Add((RotateTile(index, plan.Turns) + plan.Offset, air.Clone()));
        }

        return result;
    }

    private List<Decal> SnapshotDecals(EntityUid pulledGrid)
    {
        var result = new List<Decal>();
        if (!TryComp(pulledGrid, out DecalGridComponent? decalGrid))
            return result;

        foreach (var chunk in decalGrid.ChunkCollection.ChunkCollection.Values)
        {
            result.AddRange(chunk.Decals.Values);
        }

        return result;
    }

    private void RestoreDecals(Plan plan, List<Decal> decals)
    {
        var rotation = Angle.FromDegrees(90 * plan.Turns);

        foreach (var decal in decals)
        {
            var position = RotatePoint(decal.Coordinates, plan.Turns) + new Vector2(plan.Offset.X, plan.Offset.Y);
            var moved = decal.WithCoordinates(position).WithRotation(decal.Angle + rotation);
            _decals.TryAddDecal(moved, new EntityCoordinates(plan.KeeperGrid, position), out _);
        }
    }

    /// <summary>
    /// The kept grid's atmosphere makes air for the new tiles on its next revalidation, with nothing in them.
    /// This waits for that and then pours the old gas in, and wakes the tile so it spreads across the seam.
    /// </summary>
    private void UpdatePendingGas(TimeSpan now)
    {
        for (var i = _pendingGas.Count - 1; i >= 0; i--)
        {
            var pending = _pendingGas[i];

            if (TerminatingOrDeleted(pending.Grid) || !TryComp(pending.Grid, out GridAtmosphereComponent? atmos))
            {
                _pendingGas.RemoveAt(i);
                continue;
            }

            TryComp(pending.Grid, out GasTileOverlayComponent? overlay);
            var grid = (pending.Grid, atmos, overlay);

            for (var j = pending.Tiles.Count - 1; j >= 0; j--)
            {
                var (index, air) = pending.Tiles[j];

                // A tile that is not there yet, or still just the space around the grid, reads as an immutable
                // mixture. The real, empty mixture of a new tile shows up once the atmosphere has revalidated it.
                if (_atmos.GetTileMixture(grid, null, index) is not { Immutable: false } target)
                    continue;

                _atmos.Merge(target, air);
                _atmos.GetTileMixture(grid, null, index, excite: true);

                pending.Tiles[j] = pending.Tiles[^1];
                pending.Tiles.RemoveAt(pending.Tiles.Count - 1);
            }

            if (pending.Tiles.Count == 0 || now >= pending.Deadline)
                _pendingGas.RemoveAt(i);
        }
    }
}

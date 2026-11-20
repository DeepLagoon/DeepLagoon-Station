using Content.Shared.Tiles;
using Robust.Server.Physics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.Server.Explosion.EntitySystems;

/// <summary>
/// Applies the complete floor-damage result through the normal map API in bounded batches.
/// Tile events can unanchor machines, spawn cut cables and rebuild collision: they are work,
/// not a cheap final assignment after the explosion's time budget has expired.
/// </summary>
internal sealed class ExplosionTileCommit(IEntityManager entities, SharedMapSystem maps, ExplosionSystem system) : IDisposable
{
    internal const int TilesPerBatch = 16;

    private sealed class PendingGrid(Entity<MapGridComponent> grid)
    {
        public readonly Entity<MapGridComponent> Grid = grid;
        public readonly List<(Vector2i, Tile)> Changes = new();
        public int Applied;
    }

    private readonly Dictionary<EntityUid, PendingGrid> _grids = new();
    private readonly Queue<PendingGrid> _pending = new();
    private readonly List<(Vector2i, Tile)> _batch = new(TilesPerBatch);
    private readonly HashSet<EntityUid> _leased = new();
    private readonly Queue<(Entity<MapGridComponent> Grid, IDisposable Scope)> _splitPending = new();

    public bool Finished => _pending.Count == 0 && _splitPending.Count == 0;

    public List<(Vector2i, Tile)> GetChanges(Entity<MapGridComponent> grid)
    {
        if (!_grids.TryGetValue(grid.Owner, out var pending))
        {
            pending = new(grid);
            _grids.Add(grid.Owner, pending);
            _pending.Enqueue(pending);
        }
        return pending.Changes;
    }

    /// <param name="hasTime">Uses the same stopwatch as explosion preparation and damage.</param>
    /// <param name="effectsFinished">Do not split a grid while later blast tiles still use its coordinates.</param>
    public void Flush(Func<bool> hasTime, bool effectsFinished)
    {
        while (_pending.TryPeek(out var pending) && hasTime())
        {
            var grid = pending.Grid;
            if (!IsLive(grid) ||
                entities.TryGetComponent<ProtectedGridComponent>(grid.Owner, out var protection) && protection.PreventExplosions ||
                pending.Applied == pending.Changes.Count)
            {
                _pending.Dequeue();
                _grids.Remove(grid.Owner);
                continue;
            }

            // Native splitting can reparent not-yet-applied tiles onto another grid. Keep the
            // original frame until ALL pending changes are committed, then run the native
            // split check. Collision, atmosphere and tile-change callbacks still run per batch.
            if (grid.Comp.CanSplit && _leased.Add(grid.Owner))
            {
                _splitPending.Enqueue((grid, entities.System<GridFixtureSystem>().DeferSplitting(grid.Owner)));
            }

            _batch.Clear();
            var count = Math.Min(TilesPerBatch, pending.Changes.Count - pending.Applied);
            for (var i = 0; i < count; i++)
                _batch.Add(pending.Changes[pending.Applied + i]);

            using (system.MeasureConsequence(ExplosionSystem.ConsequencePhase.TileCommit, grid.Owner))
                maps.SetTiles(grid, _batch);
            pending.Applied += count;
        }

        if (!effectsFinished || _pending.Count != 0)
            return;

        while (_splitPending.Count > 0 && hasTime())
            RestoreSplit(_splitPending.Dequeue());
    }

    private bool IsLive(Entity<MapGridComponent> grid)
    {
        return entities.TryGetComponent<MapGridComponent>(grid.Owner, out var current) && ReferenceEquals(current, grid.Comp) &&
               entities.TryGetComponent<MetaDataComponent>(grid.Owner, out var metadata) &&
               metadata.EntityLifeStage < EntityLifeStage.Terminating;
    }

    private void RestoreSplit((Entity<MapGridComponent> Grid, IDisposable Scope) pending)
    {
        var (grid, scope) = pending;
        _leased.Remove(grid.Owner);
        using (system.MeasureConsequence(ExplosionSystem.ConsequencePhase.GridSplit, grid.Owner))
            scope.Dispose();
    }

    public void Dispose()
    {
        // Round reset, map removal, shutdown and exception paths must release every grid.
        Exception? failure = null;
        while (_splitPending.TryDequeue(out var pending))
        {
            try { RestoreSplit(pending); }
            catch (Exception e) { failure ??= e; }
        }
        _leased.Clear();
        _pending.Clear();
        _grids.Clear();
        if (failure != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

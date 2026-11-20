using Content.Server._DeepLagoon.Mapping;
using Content.Server._DeepLagoon.Performance;
using Content.Server.StationEvents.Components;
using Content.Server._NF.StationEvents.Components;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Utility;
using LoadClock = System.Diagnostics.Stopwatch;

namespace Content.Server._NF.StationEvents.Events;

public sealed partial class BluespaceErrorRule
{
    private sealed class EventLoad(EntityUid rule, IEnumerator<object?> steps, TimeSpan started, TimeSpan? duration)
    {
        public readonly EntityUid Rule = rule;
        public readonly IEnumerator<object?> Steps = steps;
        public readonly TimeSpan Started = started;
        public readonly TimeSpan? Duration = duration;
    }

    private readonly Queue<EventLoad> _loading = new();
    private readonly Dictionary<EntityUid, EventLoad> _eventLoads = new();
    private readonly Queue<EntityUid> _cancelledLoadEntities = new();
    private static readonly object WaitingForGridRead = new();

    private void QueueEventLoad(EntityUid rule, BluespaceErrorRuleComponent component)
    {
        CancelEventLoad(rule);
        TimeSpan? duration = null;
        if (TryComp<StationEventComponent>(rule, out var station))
        {
            duration = station.EndTime - Timing.CurTime;
            station.EndTime = null;
        }
        var load = new EventLoad(rule, SpawnEventSteps(rule, component).GetEnumerator(), Timing.CurTime, duration);
        _eventLoads.Add(rule, load);
        _loading.Enqueue(load);
    }

    private void CancelEventLoad(EntityUid rule)
    {
        if (_eventLoads.Remove(rule, out var load)) load.Steps.Dispose();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        // One shared budget, not one budget per event/grid. Normal ECS and simulation
        // always remain on the game thread; no global node, NPC or physics pause is used.
        var start = LoadClock.GetTimestamp();
        var operations = 0;
        while (operations++ < 1024 && LoadClock.GetElapsedTime(start).TotalMilliseconds < 3)
        {
            if (_cancelledLoadEntities.TryDequeue(out var entity))
            {
                if (!Exists(entity)) continue;
                // The allocated set can list the grid before its thousands of children.
                // Never turn cancellation into a recursive whole-grid delete in this slice.
                if (HasComp<MapComponent>(entity) || HasComp<MapGridComponent>(entity))
                    EntityManager.System<StagedMapDeletionSystem>().Queue(entity);
                else
                    Del(entity);
                continue;
            }
            if (!_loading.TryPeek(out var load)) break;
            if (!_eventLoads.ContainsKey(load.Rule)) { _loading.Dequeue(); continue; }
            if (TerminatingOrDeleted(load.Rule) || !GameTicker.IsGameRuleActive(load.Rule))
            {
                CancelEventLoad(load.Rule);
                continue;
            }
            try
            {
                if (load.Steps.MoveNext())
                {
                    if (ReferenceEquals(load.Steps.Current, WaitingForGridRead)) break;
                    continue;
                }
                load.Steps.Dispose();
                _eventLoads.Remove(load.Rule);
                _loading.Dequeue();
                if (TryComp<StationEventComponent>(load.Rule, out var station) && load.Duration is { } duration)
                    station.EndTime = Timing.CurTime + duration;
            }
            catch (Exception e)
            {
                Log.Error($"Bluespace staged load failed for {load.Rule}: {e}");
                CancelEventLoad(load.Rule);
                _loading.Dequeue();
                if (TryComp<StationEventComponent>(load.Rule, out var station) && load.Duration is { } duration)
                    station.EndTime = Timing.CurTime + duration;
            }
        }
        var elapsed = LoadClock.GetElapsedTime(start).TotalMilliseconds;
        if (elapsed >= 33.33)
            _performanceLog.Warning($"Slow bluespace load slice: elapsed_ms={elapsed:F2} operations={operations} pending_events={_eventLoads.Count} cleanup={_cancelledLoadEntities.Count}");
    }

    private static ResPath GetLastSelectedPath(BluespaceGridSpawnGroup group) => group.Paths[^1];

    private IEnumerable<object?> LoadEventGrid(EntityUid rule, MapId stagingMap, BluespaceGridSpawnGroup group,
        int index, BluespaceEventTiming? timing, Action<Entity<MapGridComponent>?> completed)
    {
        if (group.Paths.Count == 0) yield break;
        var selected = _random.Next(group.Paths.Count - index % group.Paths.Count);
        var path = group.Paths[selected];
        group.Paths.RemoveAt(selected);
        group.Paths.Add(path);
        var options = BluespaceEventTiming.MeasureMapLoad(timing, "load_grid_incremental", path, DeserializationOptions.Default);
        var stage = _map.GetMap(stagingMap);
        using var job = _loader.BeginIncrementalGridLoad(stagingMap, path, options);
        var accepted = false;
        var started = LoadClock.GetTimestamp();
        try
        {
            while (job.MoveNext()) yield return job.Phase == GridLoadPhase.Read ? WaitingForGridRead : null;
            if (job.RequiresSynchronousLoad)
            {
                // Preserve support for saved live grids with offset clocks. Release event
                // assets are pre-mapinit; unsupported files retain the original contract.
                _performanceLog.Warning($"Bluespace live-save fallback: rule={rule} file={path}");
                if (_loader.TryLoadGridTracked(stagingMap, path, out var legacy, options))
                { completed(legacy); accepted = true; }
                yield break;
            }
            if (job.Grid is not { } grid) yield break;
            GridMapSourceExtensions.Record(grid, path);
            completed(grid);
            accepted = true;
            _performanceLog.Info(FormattableString.Invariant($"Bluespace staged grid ready: rule={rule} file={path} entities={job.Entities.Count} wall_ms={LoadClock.GetElapsedTime(started).TotalMilliseconds:F2} game_thread_ms={job.CpuMilliseconds:F2} read_yaml_ms={job.ReadTime.TotalMilliseconds:F2} allocate_ms={job.GetPhaseTime(GridLoadPhase.Allocate).TotalMilliseconds:F2} components_ms={job.GetPhaseTime(GridLoadPhase.Components).TotalMilliseconds:F2} hierarchy_ms={job.GetPhaseTime(GridLoadPhase.Hierarchy).TotalMilliseconds:F2} startup_ms={job.GetPhaseTime(GridLoadPhase.Startup).TotalMilliseconds:F2} map_init_ms={job.GetPhaseTime(GridLoadPhase.MapInit).TotalMilliseconds:F2} max_step_ms={job.MaximumStepMilliseconds:F2} max_step_phase={job.MaximumStepPhase} max_step_gc_pause_ms={job.MaximumStepGcPauseMilliseconds:F2}"));
        }
        finally
        {
            if (!accepted)
            {
                foreach (var entity in job.Entities) _cancelledLoadEntities.Enqueue(entity);
                EntityManager.System<StagedMapDeletionSystem>().Queue(stage);
            }
        }
    }

    public override void Shutdown()
    {
        foreach (var load in _eventLoads.Values) load.Steps.Dispose();
        _eventLoads.Clear();
        _loading.Clear();
        while (_cancelledLoadEntities.TryDequeue(out var entity)) if (Exists(entity)) Del(entity);
        base.Shutdown();
    }
}

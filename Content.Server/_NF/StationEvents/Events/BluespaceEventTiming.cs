using Robust.Shared.EntitySerialization;
using Robust.Shared.Utility;
using Content.Server._DeepLagoon.Diagnostics;
using Robust.Shared.Log;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server._NF.StationEvents.Events;

public sealed class BluespaceEventTiming(ISawmill log, string eventId, EntityUid rule, TimeSpan budget, bool detailedCleanup = false)
{
    private static readonly PerformanceLogWriter CleanupWriter = new();
    public static DeserializationOptions MeasureMapLoad(BluespaceEventTiming? timing, string phase, ResPath file,
        DeserializationOptions options)
    {
#if !DEEPLAGOON_STOCK_ENGINE
        if (timing != null)
            options.ReportMapLoadTimings = times => timing.MapLoaded(phase, file, times);
#endif
        return options;
    }

#if !DEEPLAGOON_STOCK_ENGINE
    private void MapLoaded(string phase, ResPath file, MapLoadTimings times)
    {
        if (times.Elapsed < budget)
            return;

        log.Warning(FormattableString.Invariant(
            $"Slow bluespace operation: event={eventId} rule={rule} phase={phase} file={file} entities={times.EntityCount} elapsed_ms={times.Elapsed.TotalMilliseconds:F2} read_yaml_ms={times.ReadYaml.TotalMilliseconds:F2} prepare_ms={times.Prepare.TotalMilliseconds:F2} create_ms={times.CreateEntities.TotalMilliseconds:F2} initialize_ms={times.InitializeEntities.TotalMilliseconds:F2} tick_budget_ms={budget.TotalMilliseconds:F2}"));
    }

    public void RoomEntities(string room, EntityUid grid, long start, EntitySpawnTimings entities)
    {
        var elapsed = Stopwatch.GetElapsedTime(start);
        if (elapsed < budget)
            return;

        log.Warning(FormattableString.Invariant(
            $"Slow bluespace operation: event={eventId} rule={rule} phase=dungeon_room_entities room={room} grid={grid} entities={entities.Count} elapsed_ms={elapsed.TotalMilliseconds:F2} create_ms={entities.Create.TotalMilliseconds:F2} initialize_ms={entities.Initialize.TotalMilliseconds:F2} tick_budget_ms={budget.TotalMilliseconds:F2}"));
    }
#endif

    public void Deletion(string phase, EntityUid target, long start)
    {
        var elapsed = Stopwatch.GetElapsedTime(start);
        if (elapsed < budget)
            return;

        log.Warning(FormattableString.Invariant(
            $"Slow bluespace operation: event={eventId} rule={rule} phase={phase} target={target} elapsed_ms={elapsed.TotalMilliseconds:F2} tick_budget_ms={budget.TotalMilliseconds:F2}"));
    }

    public void GridCleanup(EntityUid grid, long start, long deleteStart, long deleteEnd)
    {
        var end = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(start, end);
        if (elapsed < budget)
            return;

        log.Warning(FormattableString.Invariant(
            $"Slow bluespace operation: event={eventId} rule={rule} phase=delete_grid grid={grid} elapsed_ms={elapsed.TotalMilliseconds:F2} prepare_ms={Stopwatch.GetElapsedTime(start, deleteStart).TotalMilliseconds:F2} delete_ms={Stopwatch.GetElapsedTime(deleteStart, deleteEnd).TotalMilliseconds:F2} after_delete_ms={Stopwatch.GetElapsedTime(deleteEnd, end).TotalMilliseconds:F2} tick_budget_ms={budget.TotalMilliseconds:F2}"));
    }

    public void GridRetirement(EntityUid grid, EntityUid retiredMap, int grids, int protectedRoots,
        long start, long retirementStart, long created, long paused, long moved, long released, long queued, long end)
    {
        var elapsed = Stopwatch.GetElapsedTime(start, end);
        if (elapsed < budget && !detailedCleanup)
            return;

        var level = elapsed >= budget ? LogLevel.Warning : LogLevel.Info;
        CleanupWriter.Write(log, FormattableString.Invariant(
            $"Bluespace grid retired: event={eventId} rule={rule} grid={grid} retired_map={retiredMap} family_roots={grids} protected_roots={protectedRoots} elapsed_ms={elapsed.TotalMilliseconds:F2} prepare_ms={Stopwatch.GetElapsedTime(start, retirementStart).TotalMilliseconds:F2} retire_ms={Stopwatch.GetElapsedTime(retirementStart, queued).TotalMilliseconds:F2} create_map_ms={Stopwatch.GetElapsedTime(retirementStart, created).TotalMilliseconds:F2} pause_ms={Stopwatch.GetElapsedTime(created, paused).TotalMilliseconds:F2} map_move_ms={Stopwatch.GetElapsedTime(paused, moved).TotalMilliseconds:F2} lifecycle_release_ms={Stopwatch.GetElapsedTime(moved, released).TotalMilliseconds:F2} queue_ms={Stopwatch.GetElapsedTime(released, queued).TotalMilliseconds:F2} finish_ms={Stopwatch.GetElapsedTime(queued, end).TotalMilliseconds:F2} tick_budget_ms={budget.TotalMilliseconds:F2} deletion_staged=True"),
            level, static () => true);
    }
}

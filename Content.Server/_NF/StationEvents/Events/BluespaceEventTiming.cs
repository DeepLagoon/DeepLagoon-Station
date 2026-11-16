using Robust.Shared.EntitySerialization;
using Robust.Shared.Utility;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server._NF.StationEvents.Events;

public sealed class BluespaceEventTiming(ISawmill log, string eventId, EntityUid rule, TimeSpan budget)
{
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
}

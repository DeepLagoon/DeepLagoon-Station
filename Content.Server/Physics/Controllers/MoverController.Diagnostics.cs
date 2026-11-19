using System;
using Robust.Shared.Log;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server.Physics.Controllers;

public sealed partial class MoverController
{
    private struct MovementSample
    {
        public long Total, Collect, Mob, Calculate, Apply, ParallelOther, Pilots, Shuttles;
        public int Movers, Parallel;
        public uint Tick;
    }

    private MovementSample _movementTotal, _movementSlowest;
    private long _movementWindow, _movementNextSummary, _movementNextSlow;
    private long _movementSamples, _movementTicks, _movementMax;
    private uint _movementLastTick;
    private int _movementMaxMovers, _movementMaxParallel;
    private static readonly double MovementMs = 1000d / Stopwatch.Frequency;
    private volatile bool _movementDiagnostics;
    private readonly Content.Server._DeepLagoon.Diagnostics.PerformanceLogWriter _movementLogWriter = new();

    private void WriteMovementWarning(string message) => _movementLogWriter.Write(Log, message, LogLevel.Warning, () => _movementDiagnostics);
    private void WriteMovementInfo(string message) => _movementLogWriter.Write(Log, message, LogLevel.Info, () => _movementDiagnostics);
    private long _movementSummaryPeriod = 30 * Stopwatch.Frequency;
    private long _movementSlowPeriod = 10 * Stopwatch.Frequency;

    private void InitializeMovementDiagnostics()
    {
        Subs.CVar(_movementConfig, Robust.Shared.CVars.PerformanceLogLevel, value =>
        {
            _movementDiagnostics = value > 0;
            ResetMovementDiagnostics();
        }, true);
        Subs.CVar(_movementConfig, Robust.Shared.CVars.PerformanceLogInterval, value =>
        {
            _movementSummaryPeriod = (long)((float.IsFinite(value) ? Math.Clamp(value, 5, 600) : 30) * Stopwatch.Frequency);
            ResetMovementDiagnostics();
        }, true);
        Subs.CVar(_movementConfig, Robust.Shared.CVars.PerformanceSlowInterval, value =>
        {
            _movementSlowPeriod = (long)((float.IsFinite(value) ? Math.Clamp(value, 1, 120) : 10) * Stopwatch.Frequency);
            ResetMovementDiagnostics();
        }, true);
    }

    private void ResetMovementDiagnostics()
    {
        _movementWindow = _movementNextSummary = _movementNextSlow = 0;
        _movementTotal = _movementSlowest = default;
        _movementSamples = _movementTicks = _movementMax = 0;
        _movementMaxMovers = _movementMaxParallel = 0;
    }

    private bool BeginMovementTiming()
    {
        return _movementDiagnostics && (Log.IsLogLevelEnabled(LogLevel.Info) || Log.IsLogLevelEnabled(LogLevel.Warning));
    }

    private void RecordMovementTiming(long start, long collected, long prepared, long moved, long pilots, long end,
        long calculate, long apply, int movers, int parallel)
    {
        if (_movementWindow == 0)
        {
            _movementWindow = start;
            _movementNextSummary = start + _movementSummaryPeriod;
            _movementNextSlow = start + _movementSlowPeriod;
        }
        var tick = Timing.CurTick.Value;
        var sample = new MovementSample
        {
            Total = end - start, Collect = collected - start, Mob = prepared - collected,
            Calculate = calculate, Apply = apply, ParallelOther = Math.Max(0, moved - prepared - calculate - apply),
            Pilots = pilots - moved, Shuttles = end - pilots, Movers = movers, Parallel = parallel, Tick = tick,
        };
        _movementSamples++;
        if (_movementTicks == 0 || tick != _movementLastTick) { _movementTicks++; _movementLastTick = tick; }
        _movementMax = Math.Max(_movementMax, sample.Total);
        _movementMaxMovers = Math.Max(_movementMaxMovers, movers);
        _movementMaxParallel = Math.Max(_movementMaxParallel, parallel);
        _movementTotal.Total += sample.Total;
        _movementTotal.Collect += sample.Collect;
        _movementTotal.Mob += sample.Mob;
        _movementTotal.Calculate += sample.Calculate;
        _movementTotal.Apply += sample.Apply;
        _movementTotal.ParallelOther += sample.ParallelOther;
        _movementTotal.Pilots += sample.Pilots;
        _movementTotal.Shuttles += sample.Shuttles;
        if (sample.Total >= Stopwatch.Frequency / 200 && sample.Total > _movementSlowest.Total)
            _movementSlowest = sample;

        // Scalar aggregation above; no per-entity clocks, allocations or string formatting.
        if (end >= _movementNextSlow)
        {
            if (_movementSlowest.Total > 0 && Log.IsLogLevelEnabled(LogLevel.Warning))
            {
                var slow = _movementSlowest;
                WriteMovementWarning($"Slow mover substep: tick={slow.Tick} elapsed_ms={slow.Total * MovementMs:F3} collect_ms={slow.Collect * MovementMs:F3} mob_prepare_or_serial_ms={slow.Mob * MovementMs:F3} batch_calc_ms={slow.Calculate * MovementMs:F3} batch_apply_ms={slow.Apply * MovementMs:F3} batch_other_ms={slow.ParallelOther * MovementMs:F3} pilots_ms={slow.Pilots * MovementMs:F3} shuttles_ms={slow.Shuttles * MovementMs:F3} movers={slow.Movers} parallel={slow.Parallel}");
            }
            _movementSlowest = default;
            _movementNextSlow = end + _movementSlowPeriod;
        }
        if (end < _movementNextSummary) return;
        if (Log.IsLogLevelEnabled(LogLevel.Info))
        {
            var scale = MovementMs / _movementTicks;
            WriteMovementInfo($"Mover summary: window_s={(end - _movementWindow) / (double)Stopwatch.Frequency:F1} ticks={_movementTicks} substeps={_movementSamples} substeps_per_tick={_movementSamples / (double)_movementTicks:F2} avg_ms_per_tick={_movementTotal.Total * scale:F3} max_substep_ms={_movementMax * MovementMs:F3} collect_ms_per_tick={_movementTotal.Collect * scale:F3} mob_prepare_or_serial_ms_per_tick={_movementTotal.Mob * scale:F3} batch_calc_ms_per_tick={_movementTotal.Calculate * scale:F3} batch_apply_ms_per_tick={_movementTotal.Apply * scale:F3} batch_other_ms_per_tick={_movementTotal.ParallelOther * scale:F3} pilots_ms_per_tick={_movementTotal.Pilots * scale:F3} shuttles_ms_per_tick={_movementTotal.Shuttles * scale:F3} movers_max={_movementMaxMovers} parallel_max={_movementMaxParallel}");
        }
        _movementTotal = default;
        _movementSamples = _movementTicks = _movementMax = 0;
        _movementMaxMovers = _movementMaxParallel = 0;
        _movementWindow = end;
        _movementNextSummary = end + _movementSummaryPeriod;
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Robust.Shared.ContentPack;
using System.Threading;
using Content.Server._DeepLagoon.Cleanup;
using Content.Server.Administration.Managers;
using Content.Server.Administration;
using Content.Server.Administration.Logs;
using Content.Server.EUI;
using Content.Shared._DeepLagoon.Performance;
using Content.Shared.Administration;
using Content.Shared.Database;
using Content.Server.GameTicking;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Serilog.Events;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server._DeepLagoon.Diagnostics;

[CVarDefs]
public sealed class PerformanceMonitorCVars
{
    public static readonly CVarDef<bool> JournalEnabled = CVarDef.Create("performance_monitor.journal_enabled", true, CVar.SERVERONLY);
    public static readonly CVarDef<float> SampleInterval = CVarDef.Create("performance_monitor.sample_interval", 5f, CVar.SERVERONLY);
}

public sealed partial class PerformanceMonitorSystem : EntitySystem
{
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private IGameTiming _clock = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private ILogManager _logs = default!;
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private EuiManager _euis = default!;
    [Dependency] private AgeCleanupSystem _cleanup = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private GameTicker _ticker = default!;
    private PerformanceJournal _journal = default!;
    private bool _journalEnabled;
    private bool _shuttingDown;
    private float _sampleInterval = 5;
    private double _utcBase;
    private int _round = -1;
    private readonly Dictionary<string, PerformanceSystemTiming> _detailedSystems = new();
    private readonly Dictionary<string, PerformancePvsComponent> _pvsComponents = new();
    private Dictionary<string, double> _context = new();
    private readonly HashSet<PerformanceMonitorEui> _viewers = new();
    private readonly Queue<PerformanceMonitorSample> _history = new();
    private readonly Dictionary<string, (long Ticks, int Calls, int Count)> _phases = new();
    private readonly ConcurrentQueue<(long At, string Line, bool Memory)> _summaries = new();
    private PerformanceMonitorPhase[] _latestPhases = [];
    private SummaryLogHandler _logHandler = default!;
    private ISawmill _tickLog = default!;
    private ISawmill _memoryLog = default!;
    private TimeSpan _nextSample, _nextState;
    private double _previousSample;
    private uint _previousSampleTick;
    private long _previousAllocated;
    private TimeSpan _previousPause;
    private volatile bool _listening;
    private int _queuedSummaries, _detailLevel;
    private long _droppedSummaries;
    private long _summaryAt;
    private Dictionary<string, double> _summary = new();
    private PerformanceSystemTiming[] _systems = [];
    private Dictionary<string, double> _memorySummary = new();
    private long _memoryAt;

    public int ViewerCount => _viewers.Count;
    public int HistoryCount => _history.Count;
    internal int CurrentRound => _ticker.RoundId;
    public bool CanRead(ICommonSession player) => _admins.HasAdminFlag(player, AdminFlags.Debug);

    public override void Initialize()
    {
        base.Initialize();
        _tickLog = _logs.GetSawmill("srv.tick_timing");
        _memoryLog = _logs.GetSawmill("srv.memory");
        _logHandler = new SummaryLogHandler(this);
        _utcBase = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d - _clock.RealTime.TotalSeconds;
        _journal = new PerformanceJournal(_resources.UserData.RootDir is { } root ? Path.Combine(root, "performance-monitor") : null);
        _previousSample = _clock.RealTime.TotalSeconds;
        _previousSampleTick = _clock.CurTick.Value;
        _previousAllocated = GC.GetTotalAllocatedBytes(false);
        _previousPause = GC.GetTotalPauseDuration();
        Subs.CVar(_configuration, PerformanceMonitorCVars.SampleInterval, value =>
        { _sampleInterval = float.IsFinite(value) ? Math.Clamp(value, 1, 60) : 5; _nextSample = _clock.RealTime + TimeSpan.FromSeconds(_sampleInterval); }, true);
        Subs.CVar(_configuration, PerformanceMonitorCVars.JournalEnabled, value =>
        { _journalEnabled = value; UpdateListening(); }, true);
        _admins.OnPermsChanged += OnPermissionsChanged;
        Subs.CVar(_configuration, Robust.Shared.CVars.PerformanceLogLevel, level =>
        {
            _detailLevel = level;
            RefreshDiagnosticSubscription();
        }, true);
    }

    public override void Shutdown()
    {
        _shuttingDown = true;
        _listening = false;
        _journal.Complete();
        _admins.OnPermsChanged -= OnPermissionsChanged;
        _tickLog.RemoveHandler(_logHandler);
        _memoryLog.RemoveHandler(_logHandler);
        EntityManager.DiagnosticWorkTiming -= OnDiagnostic;
        foreach (var viewer in _viewers.ToArray())
            viewer.Close();
        base.Shutdown();
    }

    public bool Open(ICommonSession player)
    {
        if (!CanRead(player))
            return false;
        _euis.OpenEui(new PerformanceMonitorEui(this), player);
        return true;
    }

    internal void Subscribe(PerformanceMonitorEui viewer)
    {
        if (!CanRead(viewer.Player))
        {
            viewer.Close();
            return;
        }
        _viewers.Add(viewer);
        UpdateListening();
        viewer.StateDirty();
        viewer.LoadHistory(120, 0);
    }

    internal void Unsubscribe(PerformanceMonitorEui viewer)
    {
        _viewers.Remove(viewer);
        UpdateListening();
    }

    private void UpdateListening()
    {
        var enabled = !_shuttingDown && (_journalEnabled || _viewers.Count > 0);
        if (enabled != _listening)
        {
            _listening = enabled;
            if (enabled) { _tickLog.AddHandler(_logHandler); _memoryLog.AddHandler(_logHandler); }
            else { _tickLog.RemoveHandler(_logHandler); _memoryLog.RemoveHandler(_logHandler); }
        }
        RefreshDiagnosticSubscription();
    }

    private void RefreshDiagnosticSubscription()
    {
        // Sampling preserves the engine's existing callback and never changes operator settings.
        EntityManager.DiagnosticWorkTiming -= OnDiagnostic;
        if (_listening && _detailLevel > 0)
            EntityManager.DiagnosticWorkTiming += OnDiagnostic;
    }

    private void OnPermissionsChanged(AdminPermsChangedEventArgs args)
    {
        foreach (var viewer in _viewers.Where(viewer => viewer.Player == args.Player).ToArray())
            if (!CanRead(viewer.Player)) viewer.Close();
    }

    private void OnDiagnostic(string name, long ticks, int count)
    {
        if (!_listening || _detailLevel <= 0 || !(name.StartsWith("npc.pathfinding.", StringComparison.Ordinal)
                || name.StartsWith("pvs.", StringComparison.Ordinal) || name.StartsWith("engine.pvs.", StringComparison.Ordinal)
                || name.StartsWith("sound.emit.", StringComparison.Ordinal) || name.StartsWith("engine.deletion_queue", StringComparison.Ordinal)))
            return;
        if (_phases.Count >= 32 && !_phases.ContainsKey(name))
            return;
        _phases.TryGetValue(name, out var previous);
        _phases[name] = (previous.Ticks + Math.Max(0, ticks), previous.Calls + 1, count);
    }

    public override void Update(float frameTime)
    {
        foreach (var viewer in _viewers.ToArray()) viewer.PollHistory();
        if (!_listening || _clock.RealTime < _nextSample) return;
        _nextSample = _clock.RealTime + TimeSpan.FromSeconds(_sampleInterval);
        if (_round != _ticker.RoundId)
        {
            _round = _ticker.RoundId;
            _history.Clear(); _phases.Clear(); _latestPhases = []; _summary.Clear(); _systems = [];
            _summaryAt = 0; _memorySummary.Clear(); _memoryAt = 0; _detailedSystems.Clear(); _pvsComponents.Clear(); _context.Clear();
            foreach (var viewer in _viewers) viewer.ResetRound();
        }
        RefreshDiagnosticSubscription();
        while (_summaries.TryDequeue(out var summary))
        {
            Interlocked.Decrement(ref _queuedSummaries);
            var at = _utcBase + _clock.RealTime.TotalSeconds - Stopwatch.GetElapsedTime(summary.At).TotalSeconds;
            if (_journalEnabled) _journal.Report(_round, at, summary.Memory ? "memory" : "tick", summary.Line);
            ApplyReport(summary.Line, summary.At);
        }
        Sample();
        if (_clock.RealTime < _nextState)
            return;
        _nextState = _clock.RealTime + TimeSpan.FromSeconds(2);
        foreach (var viewer in _viewers) viewer.StateDirty();
    }

    private void Sample()
    {
        var now = _clock.RealTime.TotalSeconds;
        var window = Math.Max(.001, now - _previousSample);
        var allocated = GC.GetTotalAllocatedBytes(false);
        var pause = GC.GetTotalPauseDuration();
        var summaryAge = _summaryAt == 0 ? double.PositiveInfinity : Stopwatch.GetElapsedTime(_summaryAt).TotalSeconds;
        var summaryWindow = _summary.GetValueOrDefault("window_s");
        var fresh = summaryAge <= Math.Max(10, summaryWindow * 2.5);
        var durations = ReadPhaseDurations(_summary, fresh);
        double? Pvs(string key) => fresh && _summary.TryGetValue(key, out var value) ? value : null;
        var tick = _clock.CurTick.Value;
        var tps = MeasureTps(_previousSampleTick, tick, window);
        var sample = new PerformanceMonitorSample(_utcBase + now, window, tps,
            GC.GetTotalMemory(false) / 1048576d, Environment.WorkingSet / 1048576d,
            Math.Max(0, allocated - _previousAllocated) / 1048576d / window,
            Math.Max(0, (pause - _previousPause).TotalMilliseconds), durations.Tick, durations.Simulation,
            durations.Physics, durations.GameState, summaryWindow, double.IsFinite(summaryAge) ? summaryAge : -1)
        { PvsWaitMs = Pvs("pvs_wait_previous_avg_ms"), PvsSerializeMs = Pvs("pvs_serialize_avg_ms"), PvsSendMs = Pvs("pvs_send_avg_ms"),
            Entities = EntityManager.EntityCount, CleanupCandidates = _cleanup.TrackedCount };
        _history.Enqueue(sample);
        while (_history.Count > 720) _history.Dequeue();
        if (_journalEnabled) _journal.Record(_round, sample);
        _previousSample = now;
        _previousSampleTick = tick;
        _previousAllocated = allocated;
        _previousPause = pause;
        _latestPhases = _phases.Select(pair => new PerformanceMonitorPhase(pair.Key,
                pair.Value.Ticks * 1000d / Stopwatch.Frequency, pair.Value.Calls, pair.Value.Count))
            .OrderByDescending(phase => phase.TotalMs).Take(32).ToArray();
        if (_journalEnabled) _journal.Phases(_round, sample.Time, _latestPhases);
        _phases.Clear();
    }

    public static double MeasureTps(uint previous, uint current, double seconds)
    {
        // Server render/main-loop FPS is independent of simulation tick rate.
        if (!double.IsFinite(seconds) || seconds <= 0 || current < previous)
            return 0;
        return (current - previous) / seconds;
    }

    internal PerformanceMonitorState State(double after, string status, ICommonSession? player = null, long afterMemory = 0)
    {
        var fresh = _detailLevel > 0 && _summaryAt != 0 &&
            Stopwatch.GetElapsedTime(_summaryAt).TotalSeconds <= Math.Max(10, _summary.GetValueOrDefault("window_s") * 2.5);
        var candidates = _cleanup.GetCandidates(100).Select(candidate =>
        {
            TryGetEntity(candidate.Entity, out var uid);
            var paused = uid is { } entity && MetaData(entity).EntityPaused;
            var reason = candidate.Kind switch
            {
                AgeCleanupKind.OpenedMail => "Открытая почта",
                AgeCleanupKind.SpaceDebris => "Предмет в космосе",
                AgeCleanupKind.LooseOrgan => "Орган на полу",
                _ => "Подбитый дрон",
            };
            return new PerformanceCleanupCandidate(candidate.Entity, candidate.Name, reason,
                paused ? 0 : Math.Max(0, (_clock.CurTime - candidate.Since).TotalSeconds),
                paused ? -1 : Math.Max(0, (candidate.Due - _clock.CurTime).TotalSeconds), candidate.Units, paused);
        }).ToArray();
        return new(_history.Where(sample => sample.Time > after).ToArray(), _latestPhases, candidates,
            EntityManager.EntityCount, _cleanup.TrackedCount, _detailLevel > 0, status)
        {
            RoundId = _ticker.RoundId,
            Controls = DiagnosticState(player),
            Systems = fresh ? _systems : [],
            Memory = _memoryAt > afterMemory
                ? _memorySummary.Select(pair => new PerformanceMetric(pair.Key, pair.Value)).Take(256).ToArray() : [],
            MemoryAgeSeconds = _memoryAt == 0 ? -1 : Stopwatch.GetElapsedTime(_memoryAt).TotalSeconds,
            MemorySequence = _memoryAt,
            TickMetrics = fresh ? _summary.Select(pair => new PerformanceMetric(pair.Key, pair.Value)).Take(256).ToArray() : [],
            ContextMetrics = fresh ? _context.Select(pair => new PerformanceMetric(pair.Key, pair.Value)).Take(128).ToArray() : [],
            PvsComponents = fresh ? _pvsComponents.Values.OrderByDescending(row => row.TotalMs).Take(32).ToArray() : [],
            JournalStatus = (_journalEnabled ? _journal.Status : "Запись журнала выключена; история доступна только за время сбора.")
                + $" · пропущено входящих отчётов {Interlocked.Read(ref _droppedSummaries)}",
        };
    }

    internal int Delete(ICommonSession player, NetEntity[] entities)
    {
        if (!CanRead(player))
            return 0;
        var deleted = _cleanup.DeleteEligible(entities, 32);
        _adminLog.Add(LogType.EntityDelete, LogImpact.High,
            $"{player:actor} requested performance cleanup: requested={Math.Min(entities.Length, 32)} queued={deleted} entities={string.Join(", ", entities.Take(32))}");
        return deleted;
    }

    public readonly record struct PhaseDurations(double? Tick, double? Simulation, double? Physics, double? GameState);

    public static PhaseDurations ReadPhaseDurations(IReadOnlyDictionary<string, double> summary, bool fresh)
    {
        if (!fresh)
            return default;
        double? Metric(string name) => summary.TryGetValue(name, out var value) ? value : null;
        double? physics = null;
        for (var index = 1; index <= 10; index++)
            if (summary.TryGetValue($"physics{index}_ms", out var value)) physics = (physics ?? 0) + value;
        // Dispatch is inclusive of the PVS child phases. Adding the children counts their work twice.
        return new(Metric("tick_avg_ms"), Metric("simulation_avg_ms"), physics, Metric("pvs_dispatch_avg_ms"));
    }

    /// <summary>Parse bounded engine measurements of elapsed tick-thread phases, including submission/waits for async work.</summary>
    public static Dictionary<string, double> ParseSummary(string line)
    {
        var result = new Dictionary<string, double>();
        if (line.Length > 32768)
            return result;
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < fields.Length && index < 512; index++)
        {
            var equals = fields[index].IndexOf('=');
            if (equals <= 0)
                continue;
            var key = fields[index][..equals];
            var text = fields[index][(equals + 1)..];
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0)
                result[key] = value;
            else if (key.StartsWith("system", StringComparison.Ordinal) && text.Contains("PhysicsSystem", StringComparison.Ordinal))
            {
                var ms = fields.Skip(index + 1).Take(4).FirstOrDefault(field => field.StartsWith(key + "_avg_ms=", StringComparison.Ordinal));
                if (ms != null && double.TryParse(ms[(ms.IndexOf('=') + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                    && double.IsFinite(value) && value >= 0)
                    result["physics" + key[6..] + "_ms"] = value;
            }
        }
        return result;
    }

    private sealed class SummaryLogHandler(PerformanceMonitorSystem owner) : ILogHandler
    {
        public void Log(string sawmillName, LogEvent message)
        {
            var memory = sawmillName == "srv.memory";
            if (!owner._listening || sawmillName != "srv.tick_timing" && !memory)
                return;
            var line = message.RenderMessage(CultureInfo.InvariantCulture);
            if (line.Length > 32768 || !(memory ? line.StartsWith("Memory diagnostic summary:", StringComparison.Ordinal) :
                    line.StartsWith("Server tick timing summary:", StringComparison.Ordinal) || line.StartsWith("Performance ", StringComparison.Ordinal) ||
                    line.StartsWith("Slow server tick:", StringComparison.Ordinal) || line.StartsWith("Slow PVS session work:", StringComparison.Ordinal))) return;
            if (Interlocked.Increment(ref owner._queuedSummaries) > 256)
            {
                Interlocked.Decrement(ref owner._queuedSummaries);
                Interlocked.Increment(ref owner._droppedSummaries);
                return;
            }
            owner._summaries.Enqueue((Stopwatch.GetTimestamp(), line, memory));
        }
    }
}

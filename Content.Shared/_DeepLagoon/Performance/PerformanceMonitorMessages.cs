using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Performance;

[Serializable, NetSerializable]
public sealed record PerformanceMonitorSample(double Time, double WindowSeconds, double Tps, double ManagedMiB, double WorkingSetMiB,
    double AllocatedMiBPerSecond, double GcPauseMs, double? TickMs, double? SimulationMs, double? PhysicsMs,
    double? GameStateMs, double SummaryWindowSeconds, double SummaryAgeSeconds)
{
    public double? PvsWaitMs { get; init; }
    public double? PvsSerializeMs { get; init; }
    public double? PvsSendMs { get; init; }
    public int Entities { get; init; } = -1;
    public int CleanupCandidates { get; init; }
}

[Serializable, NetSerializable]
public sealed record PerformanceMonitorPhase(string Name, double TotalMs, int Calls, int Count);

[Serializable, NetSerializable]
public sealed record PerformanceSystemTiming(string Name, double AverageMs, double MaximumMs)
{
    public long Calls;
    public double? MaximumTickMs;
    public long? AllocatedBytes;
    public long? FailedCalls;
}

[Serializable, NetSerializable]
public sealed record PerformancePvsComponent(string Name, long Samples, double TotalMs, double AverageMs, double MaximumMs, string WorstEntity);

[Serializable, NetSerializable]
public sealed record PerformanceMetric(string Name, double Value);

[Serializable, NetSerializable]
public sealed record PerformanceDiagnosticsState(int PhaseLevel, float PhaseInterval, int MemoryLevel,
    int MemoryInterval, int AutoTraceInterval, bool MemoryAvailable, bool CanConfigure, string CaptureStatus);

[Serializable, NetSerializable]
public enum PerformanceDiagnosticAction : byte
{
    PhasesOff, PhasesSummary, PhasesDetailed,
    MemoryOff, MemorySummary, MemoryTraces, MemorySnapshots,
    AutoTraceOff, AutoTraceOn, TraceGc, TraceAllocations, StopCapture, HeapSnapshot, GcSnapshot,
    PhaseInterval10, PhaseInterval30, PhaseInterval60,
}

[Serializable, NetSerializable]
public sealed class PerformanceDiagnosticRequest(PerformanceDiagnosticAction action, bool confirmPause = false) : EuiMessageBase
{
    public PerformanceDiagnosticAction Action = action;
    public bool ConfirmPause = confirmPause;
}

[Serializable, NetSerializable]
public sealed record PerformanceCleanupCandidate(NetEntity Entity, string Name, string Reason, double AgeSeconds,
    double RemainingSeconds, int Units, bool Paused);

[Serializable, NetSerializable]
public sealed class PerformanceMonitorState(PerformanceMonitorSample[] samples, PerformanceMonitorPhase[] phases,
    PerformanceCleanupCandidate[] candidates, int entities, int trackedCandidates, bool diagnostics, string status) : EuiStateBase
{
    public PerformanceMonitorSample[] Samples = samples;
    public PerformanceMonitorPhase[] Phases = phases;
    public PerformanceCleanupCandidate[] Candidates = candidates;
    public int Entities = entities;
    public int TrackedCandidates = trackedCandidates;
    public bool Diagnostics = diagnostics;
    public string Status = status;
    public int RoundId;
    public PerformanceDiagnosticsState Controls = new(0, 30, 0, 60, 0, false, false, "Недоступно в этой сборке сервера.");
    public PerformanceSystemTiming[] Systems = [];
    public PerformanceMetric[] Memory = [];
    public double MemoryAgeSeconds = -1;
    public long MemorySequence;
    public PerformanceMetric[] TickMetrics = [];
    public PerformanceMetric[] ContextMetrics = [];
    public PerformancePvsComponent[] PvsComponents = [];
    public bool ResetHistory;
    public bool Historical;
    public double SelectedTime;
    public int HistorySeconds = 120;
    public string JournalStatus = "";
}

[Serializable, NetSerializable]
public sealed class PerformanceHistoryRequest(int seconds, double at = 0) : EuiMessageBase
{
    public int Seconds = seconds;
    public double At = at;
}

[Serializable, NetSerializable]
public sealed class PerformanceCleanupRequest(NetEntity[] entities) : EuiMessageBase
{
    public NetEntity[] Entities = entities;
}

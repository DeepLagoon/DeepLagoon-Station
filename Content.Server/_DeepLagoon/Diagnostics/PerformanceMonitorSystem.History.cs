using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Shared._DeepLagoon.Performance;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Diagnostics;

public sealed partial class PerformanceMonitorSystem
{
    internal Task<PerformanceJournalHistory> ReadHistory(int seconds, double at)
        => _journalEnabled ? _journal.Read(_ticker.RoundId, seconds, at) : Task.FromResult(new PerformanceJournalHistory([], [], _history.Count, false));

    private void ApplyReport(string line, long at)
    {
        if (line.StartsWith("Memory diagnostic summary:", StringComparison.Ordinal))
        { _memorySummary = ParseSummary(line); _memoryAt = at; }
        else if (line.StartsWith("Server tick timing summary:", StringComparison.Ordinal))
        { _summary = ParseSummary(line); _systems = ParseSystems(line); _summaryAt = at; }
        else if (line.StartsWith("Performance context:", StringComparison.Ordinal))
        { _context = ParseContext(line); _detailedSystems.Clear(); _pvsComponents.Clear(); }
        else if (line.StartsWith("Performance system:", StringComparison.Ordinal) && ParseDetailedSystem(line) is { } system)
        {
            if (_detailedSystems.Count < 32 || _detailedSystems.ContainsKey(system.Name)) _detailedSystems[system.Name] = system;
            _systems = _detailedSystems.Values.OrderByDescending(row => row.AverageMs).Take(32).ToArray();
        }
        else if (line.StartsWith("Performance PVS component sample:", StringComparison.Ordinal) && ParsePvsComponent(line) is { } component)
        { if (_pvsComponents.Count < 32 || _pvsComponents.ContainsKey(component.Name)) _pvsComponents[component.Name] = component; }
        else if (line.StartsWith("Performance PVS coverage:", StringComparison.Ordinal))
            foreach (var metric in ParseSummary(line)) _context["pvs." + metric.Key] = metric.Value;
        else if (line.StartsWith("Performance system coverage:", StringComparison.Ordinal))
            foreach (var metric in ParseSummary(line)) _context["systems." + metric.Key] = metric.Value;
    }

    internal PerformanceMonitorState Replay(PerformanceJournalHistory history, int seconds, double at, ICommonSession player)
    {
        var state = State(double.MaxValue, "", player);
        state.Samples = history.Samples.Length > 0 || at > 0 ? history.Samples : _history.ToArray();
        state.ResetHistory = true;
        state.Historical = at > 0;
        state.SelectedTime = history.Samples.LastOrDefault()?.Time ?? 0;
        state.HistorySeconds = seconds;
        state.JournalStatus += $" · строк выборок: {history.Count:N0}" + (history.Truncated ? " · достигнут предел хранения" : "");
        if (at <= 0) return state;
        state.Entities = history.Samples.LastOrDefault()?.Entities ?? -1;
        state.TrackedCandidates = history.Samples.LastOrDefault()?.CleanupCandidates ?? 0;
        state.Candidates = []; // Cleanup targets are world entities now, never historical journal entities.
        var tick = new Dictionary<string, double>(); var memory = new Dictionary<string, double>();
        var context = new Dictionary<string, double>();
        PerformanceSystemTiming[] systems = [];
        var detailed = new Dictionary<string, PerformanceSystemTiming>();
        var pvs = new Dictionary<string, PerformancePvsComponent>();
        var memoryAt = 0d;
        PerformanceMonitorPhase[] phases = [];
        foreach (var report in history.Reports)
        {
            var line = report.Line;
            if (line.StartsWith("Memory diagnostic summary:", StringComparison.Ordinal))
            { memory = ParseSummary(line); memoryAt = report.At; }
            else if (line.StartsWith("Server tick timing summary:", StringComparison.Ordinal))
            { tick = ParseSummary(line); systems = ParseSystems(line); }
            else if (line.StartsWith("Performance context:", StringComparison.Ordinal))
            { context = ParseContext(line); detailed.Clear(); pvs.Clear(); }
            else if (line.StartsWith("Performance system:", StringComparison.Ordinal) && ParseDetailedSystem(line) is { } system)
            { detailed[system.Name] = system; systems = detailed.Values.OrderByDescending(row => row.AverageMs).ToArray(); }
            else if (line.StartsWith("Performance PVS component sample:", StringComparison.Ordinal) && ParsePvsComponent(line) is { } component)
                pvs[component.Name] = component;
            else if (line.StartsWith("Performance PVS coverage:", StringComparison.Ordinal))
                foreach (var metric in ParseSummary(line)) context["pvs." + metric.Key] = metric.Value;
            else if (line.StartsWith("Performance system coverage:", StringComparison.Ordinal))
                foreach (var metric in ParseSummary(line)) context["systems." + metric.Key] = metric.Value;
            else if (line.StartsWith("Monitor phases: ", StringComparison.Ordinal))
                try { phases = JsonSerializer.Deserialize<PerformanceMonitorPhase[]>(line[16..]) ?? []; } catch (JsonException) { }
        }
        var latest = history.Samples.LastOrDefault();
        var fresh = latest != null && latest.SummaryAgeSeconds >= 0 && latest.SummaryAgeSeconds <= Math.Max(10, latest.SummaryWindowSeconds * 2.5);
        state.Systems = fresh ? systems : [];
        state.TickMetrics = fresh ? tick.Select(row => new PerformanceMetric(row.Key, row.Value)).ToArray() : [];
        state.ContextMetrics = fresh ? context.Select(row => new PerformanceMetric(row.Key, row.Value)).ToArray() : [];
        state.PvsComponents = fresh ? pvs.Values.OrderByDescending(row => row.TotalMs).ToArray() : [];
        state.Phases = phases;
        state.Memory = memory.Select(row => new PerformanceMetric(row.Key, row.Value)).ToArray();
        state.MemoryAgeSeconds = memoryAt == 0 ? -1 : Math.Max(0, state.SelectedTime - memoryAt);
        state.MemorySequence = (long)(memoryAt * 1000);
        return state;
    }

    private static Dictionary<string, string> Fields(string line)
        => line.Length > 32768 ? new() : line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(512)
            .Select(field => field.Split('=', 2)).Where(field => field.Length == 2)
            .GroupBy(field => field[0]).ToDictionary(group => group.Key, group => group.Last()[1]);

    private static Dictionary<string, double> ParseContext(string line)
    {
        var values = ParseSummary(line);
        if (Fields(line).GetValueOrDefault("process_cpu_available") is not ("True" or "true"))
        { values.Remove("process_cpu_ms"); values.Remove("process_cpu_cores"); }
        return values;
    }

    public static PerformanceSystemTiming? ParseDetailedSystem(string line)
    {
        var fields = Fields(line); var values = ParseSummary(line);
        if (!fields.TryGetValue("name", out var name) || name.Length > 160 || !values.TryGetValue("avg_ms_per_tick", out var average)) return null;
        var measured = fields.GetValueOrDefault("allocations_and_failures_measured") == "True";
        return new(name, average, values.GetValueOrDefault("max_call_ms"))
        {
            Calls = values.TryGetValue("calls", out var calls) ? (long)Math.Min(long.MaxValue, calls) : -1,
            MaximumTickMs = values.TryGetValue("max_tick_ms", out var maximum) ? maximum : null,
            AllocatedBytes = measured && values.TryGetValue("allocated_main_thread_bytes", out var allocated) ? (long)Math.Min(long.MaxValue, allocated) : null,
            FailedCalls = measured && values.TryGetValue("failed_calls", out var failed) ? (long)Math.Min(long.MaxValue, failed) : null,
        };
    }

    public static PerformancePvsComponent? ParsePvsComponent(string line)
    {
        var fields = Fields(line); var values = ParseSummary(line);
        if (!fields.TryGetValue("name", out var name) || name.Length > 160 || !values.TryGetValue("samples", out var count) || count <= 0 ||
            !values.TryGetValue("sampled_total_ms", out var total) || !values.TryGetValue("sampled_avg_ms", out var average) ||
            !values.TryGetValue("sampled_max_ms", out var maximum)) return null;
        return new(name, (long)Math.Min(long.MaxValue, count), total, average, maximum, fields.GetValueOrDefault("worst_entity_uid", "—"));
    }
}

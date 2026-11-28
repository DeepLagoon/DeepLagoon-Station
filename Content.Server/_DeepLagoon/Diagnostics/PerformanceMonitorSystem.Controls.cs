using System.Globalization;
using System.Linq;
using Content.Shared._DeepLagoon.Performance;
using Content.Shared.Administration;
using Content.Shared.Database;
using Robust.Shared.Player;
#if !DEEPLAGOON_STOCK_ENGINE && DEEPLAGOON_ENGINE_MEMORY_DIAGNOSTICS
using Robust.Server.Diagnostics;
#endif

namespace Content.Server._DeepLagoon.Diagnostics;

public sealed partial class PerformanceMonitorSystem
{
#if !DEEPLAGOON_STOCK_ENGINE && DEEPLAGOON_ENGINE_MEMORY_DIAGNOSTICS
    [Dependency] private ServerMemoryDiagnostics _memoryDiagnostics = default!;
#endif
    private TimeSpan _nextControl;

    // Match the existing host-only cvar/memdiag commands. Read-only access remains Debug.
    internal bool CanConfigure(ICommonSession player) => CanRead(player) && _admins.HasAdminFlag(player, AdminFlags.Host);

    private bool MemoryAvailable
    {
        get
        {
#if !DEEPLAGOON_STOCK_ENGINE && DEEPLAGOON_ENGINE_MEMORY_DIAGNOSTICS
            return _configuration.IsCVarRegistered("log.memory_diagnostics");
#else
            return false;
#endif
        }
    }

    private PerformanceDiagnosticsState DiagnosticState(ICommonSession? player)
    {
        var available = MemoryAvailable;
        int MemoryValue(string name) => available ? (int)_configuration.GetCVar(name) : 0;
        var status = "Нужна сборка серверного движка с диагностикой памяти.";
#if !DEEPLAGOON_STOCK_ENGINE && DEEPLAGOON_ENGINE_MEMORY_DIAGNOSTICS
        if (available) status = _memoryDiagnostics.Status;
#endif
        return new(_detailLevel, _configuration.GetCVar(Robust.Shared.CVars.PerformanceLogInterval),
            MemoryValue("log.memory_diagnostics"), MemoryValue("log.memory_interval"),
            MemoryValue("log.memory_trace_interval"), available, player != null && CanConfigure(player), status);
    }

    internal string Configure(ICommonSession player, PerformanceDiagnosticRequest request)
    {
        if (!CanConfigure(player)) return "Переключение диагностики требует активных прав Debug и Host.";
        if (!Enum.IsDefined(request.Action)) return "Неизвестное действие диагностики.";
        if (_clock.RealTime < _nextControl) return "Подождите секунду перед следующим изменением.";
        var action = request.Action;
        var phases = action is PerformanceDiagnosticAction.PhasesOff or PerformanceDiagnosticAction.PhasesSummary
            or PerformanceDiagnosticAction.PhasesDetailed;
        var interval = action is PerformanceDiagnosticAction.PhaseInterval10 or PerformanceDiagnosticAction.PhaseInterval30
            or PerformanceDiagnosticAction.PhaseInterval60;
        if (!phases && !interval && !MemoryAvailable) return "Диагностика памяти недоступна в этой сборке серверного движка.";
        if (action is PerformanceDiagnosticAction.HeapSnapshot or PerformanceDiagnosticAction.GcSnapshot && !request.ConfirmPause)
            return "Подтвердите возможную паузу игры; gcdump также вызывает полный GC.";
        _nextControl = _clock.RealTime + TimeSpan.FromSeconds(1);
        string result;
        if (phases)
        {
            var level = action == PerformanceDiagnosticAction.PhasesOff ? 0 : action == PerformanceDiagnosticAction.PhasesSummary ? 1 : 2;
            _configuration.SetCVar(Robust.Shared.CVars.PerformanceLogLevel, level);
            _systems = [];
            _phases.Clear();
            _latestPhases = [];
            result = $"Уровень диагностики фаз: {level}. Следующий отчёт придёт по интервалу сервера.";
        }
        else if (interval)
        {
            var seconds = action == PerformanceDiagnosticAction.PhaseInterval10 ? 10f : action == PerformanceDiagnosticAction.PhaseInterval30 ? 30f : 60f;
            _configuration.SetCVar(Robust.Shared.CVars.PerformanceLogInterval, seconds);
            result = $"Интервал отчётов фаз: {seconds:0} с.";
        }
        else if (action is >= PerformanceDiagnosticAction.MemoryOff and <= PerformanceDiagnosticAction.MemorySnapshots)
        {
            var level = (int)action - (int)PerformanceDiagnosticAction.MemoryOff;
            _configuration.SetCVar("log.memory_diagnostics", level);
            result = $"Уровень диагностики памяти: {level}. Авто-GC трассы определяются отдельным переключателем; снимки запускаются вручную.";
        }
        else if (action is PerformanceDiagnosticAction.AutoTraceOff or PerformanceDiagnosticAction.AutoTraceOn)
        {
            var seconds = action == PerformanceDiagnosticAction.AutoTraceOn ? 1800 : 0;
            _configuration.SetCVar("log.memory_trace_interval", seconds);
            result = seconds == 0 ? "Автоматические трассы GC выключены." : "Автоматические трассы GC: раз в 30 минут при уровне памяти 2–3.";
        }
        else
        {
#if !DEEPLAGOON_STOCK_ENGINE && DEEPLAGOON_ENGINE_MEMORY_DIAGNOSTICS
            result = action switch
            {
                PerformanceDiagnosticAction.TraceGc => _memoryDiagnostics.StartCapture("gc", 30),
                PerformanceDiagnosticAction.TraceAllocations => _memoryDiagnostics.StartCapture("allocations", 30),
                PerformanceDiagnosticAction.StopCapture => _memoryDiagnostics.CancelCapture(),
                PerformanceDiagnosticAction.HeapSnapshot => _memoryDiagnostics.StartCapture("heap", confirmPause: request.ConfirmPause),
                PerformanceDiagnosticAction.GcSnapshot => _memoryDiagnostics.StartCapture("gcdump", confirmPause: request.ConfirmPause),
                _ => "Неизвестное действие диагностики.",
            };
#else
            result = "Диагностика памяти недоступна в этой сборке.";
#endif
        }
        _adminLog.Add(LogType.AdminCommands, LogImpact.High, $"{player:actor} performance diagnostics: {action}; {result}");
        foreach (var viewer in _viewers) viewer.StateDirty();
        return result;
    }

    public static PerformanceSystemTiming[] ParseSystems(string line)
    {
        if (line.Length > 32768) return [];
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(512)
            .Select(field => field.Split('=', 2)).Where(field => field.Length == 2)
            .GroupBy(field => field[0]).ToDictionary(group => group.Key, group => group.Last()[1]);
        var result = new List<PerformanceSystemTiming>();
        for (var i = 1; i <= 32; i++)
        {
            var key = "system" + i;
            if (!fields.TryGetValue(key, out var name) || name.Length > 160 ||
                !fields.TryGetValue(key + "_avg_ms", out var text) ||
                !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var average) || !double.IsFinite(average) || average < 0)
                continue;
            var maximum = average;
            if (fields.TryGetValue(key + "_max_ms", out text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && double.IsFinite(parsed) && parsed >= 0) maximum = parsed;
            var calls = fields.TryGetValue(key + "_calls", out var callsText) &&
                long.TryParse(callsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var measuredCalls) && measuredCalls >= 0
                ? measuredCalls : -1;
            result.Add(new(name, average, maximum) { Calls = calls });
        }
        return result.OrderByDescending(row => row.AverageMs).ToArray();
    }
}

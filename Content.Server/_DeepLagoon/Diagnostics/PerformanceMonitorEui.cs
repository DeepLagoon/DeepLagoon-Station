using System.Linq;
using System.Threading.Tasks;
using Content.Server.EUI;
using Content.Shared._DeepLagoon.Performance;
using Content.Shared.Eui;

namespace Content.Server._DeepLagoon.Diagnostics;

public sealed class PerformanceMonitorEui(PerformanceMonitorSystem monitor) : BaseEui
{
    private double _lastSample = -1;
    private long _lastMemory;
    private const string WaitingStatus = "Ожидание первой секундной выборки.";
    private string _status = WaitingStatus;
    private Task<PerformanceJournalHistory>? _read;
    private PerformanceMonitorState? _replay;
    private bool _replace;
    private int _seconds = 120, _requestedRound;
    private double _at;
    private (int Seconds, double At)? _queuedRead;

    internal void LoadHistory(int seconds, double at)
    {
        if (_read is { IsCompleted: false }) { _queuedRead = (seconds, at); return; }
        _queuedRead = null;
        _seconds = seconds; _at = at; _requestedRound = monitor.CurrentRound;
        _status = "Загружается история журнала…";
        _read = monitor.ReadHistory(seconds, at);
    }

    internal void PollHistory()
    {
        if (_read is not { IsCompleted: true } read) return;
        _read = null;
        if (_queuedRead is { } queued)
        { _queuedRead = null; LoadHistory(queued.Seconds, queued.At); return; }
        if (_requestedRound != monitor.CurrentRound) { LoadHistory(_seconds, 0); return; }
        if (read.IsCompletedSuccessfully)
        {
            // Only inspect a completed successful task; this never waits on the simulation thread.
            _replay = monitor.Replay(read.GetAwaiter().GetResult(), _seconds, _at, Player);
            _replace = true;
            _lastSample = _replay.Samples.LastOrDefault()?.Time ?? -1;
            _status = _at > 0 ? "Исторический срез." : "";
        }
        else _status = "История временно недоступна.";
        StateDirty();
    }

    internal void ResetRound()
    {
        _lastSample = -1; _lastMemory = 0; _replay = null; _replace = true; _at = 0;
        LoadHistory(_seconds, 0);
    }

    public override void Opened() => monitor.Subscribe(this);
    public override void Closed() => monitor.Unsubscribe(this);

    public override EuiStateBase GetNewState()
    {
        if (!monitor.CanRead(Player))
            return new PerformanceMonitorState([], [], [], 0, 0, false, "Нет доступа.");
        if (_replay != null && (_replace || _at > 0))
        {
            var replay = _replay;
            if (!_replace) replay.Samples = [];
            replay.ResetHistory = _replace;
            replay.Status = _status;
            _replace = false;
            return replay;
        }
        var state = monitor.State(_lastSample, _status, Player, _lastMemory);
        state.HistorySeconds = _seconds;
        state.ResetHistory = _replace;
        _replace = false;
        _lastMemory = state.MemorySequence;
        if (state.Samples.Length > 0)
        {
            _lastSample = state.Samples[^1].Time;
            if (_status == WaitingStatus) _status = "";
        }
        return state;
    }

    public override void HandleMessage(EuiMessageBase message)
    {
        base.HandleMessage(message);
        if (IsShutDown || !monitor.CanRead(Player))
            return;
        if (message is PerformanceHistoryRequest history)
        {
            if (history.Seconds is not (0 or 30 or 60 or 120 or 600 or 1800 or 3600 or 21600 or 86400 or 604800) ||
                !double.IsFinite(history.At) || history.At < 0) return;
            LoadHistory(history.Seconds, history.At);
            StateDirty(); return;
        }
        if (message is PerformanceDiagnosticRequest diagnostic)
        {
            _status = monitor.Configure(Player, diagnostic);
            StateDirty();
            return;
        }
        if (message is not PerformanceCleanupRequest request)
            return;
        if (request.Entities is not { Length: <= 32 })
        {
            _status = "Лимит удаления: 32 объекта.";
            StateDirty();
            return;
        }
        var count = monitor.Delete(Player, request.Entities.Distinct().ToArray());
        _status = $"На удаление поставлено: {count}.";
        StateDirty();
    }
}

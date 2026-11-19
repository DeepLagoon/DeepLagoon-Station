using System.Threading;
using Robust.Shared.Log;

namespace Content.Server._DeepLagoon.Diagnostics;

/// <summary>One pending message per severity/producer; background code only sees immutable text and an enable flag.</summary>
internal sealed class PerformanceLogWriter
{
    private int _infoPending, _warningPending;
    private long _dropped;

    public void Write(ISawmill log, string message, LogLevel level, Func<bool> enabled)
    {
        var busy = level >= LogLevel.Warning
            ? Interlocked.CompareExchange(ref _warningPending, 1, 0)
            : Interlocked.CompareExchange(ref _infoPending, 1, 0);
        if (busy != 0)
        { Interlocked.Increment(ref _dropped); return; }
        try
        {
            var item = new Pending(this, log, message + " diagnostic_reports_dropped=" + Interlocked.Read(ref _dropped), level, enabled);
            if (ThreadPool.QueueUserWorkItem(static value => value.Run(), item, preferLocal: false)) return;
        }
        catch (Exception) { }
        Interlocked.Increment(ref _dropped);
        Complete(level);
    }

    private void Complete(LogLevel level)
    {
        if (level >= LogLevel.Warning) Volatile.Write(ref _warningPending, 0);
        else Volatile.Write(ref _infoPending, 0);
    }

    private sealed class Pending(PerformanceLogWriter owner, ISawmill log, string message, LogLevel level, Func<bool> enabled)
    {
        public void Run()
        {
            try { if (enabled()) log.Log(level, message); }
            catch (Exception) { Interlocked.Increment(ref owner._dropped); }
            finally { owner.Complete(level); }
        }
    }
}

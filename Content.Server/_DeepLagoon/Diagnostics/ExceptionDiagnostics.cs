using System.Linq;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace Content.Server._DeepLagoon.Diagnostics;

/// <summary>Exception types and call stacks, without messages, Data or application values.</summary>
public static class ExceptionDiagnostics
{
    private sealed class Site
    {
        public long Next;
        public int Suppressed;
    }

    private static readonly ConcurrentDictionary<(string File, int Line, Type Type), Site> Sites = new();

    public static string Describe(Exception error, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var site = Sites.GetOrAdd((file, line, error.GetType()), _ => new Site());
        int suppressed;
        lock (site)
        {
            var now = Stopwatch.GetTimestamp();
            if (now < site.Next)
            {
                site.Suppressed++;
                return $"{error.GetType().Name} (repeated stack suppressed)";
            }
            site.Next = now + 30 * Stopwatch.Frequency;
            suppressed = site.Suppressed;
            site.Suppressed = 0;
        }

        var text = new StringBuilder();
        var pending = new Queue<Exception>();
        pending.Enqueue(error);
        for (var count = 0; pending.TryDequeue(out var current) && count < 8; count++)
        {
            if (count > 0) text.AppendLine("Caused by:");
            text.AppendLine(current.GetType().FullName);
            text.AppendLine(current.StackTrace ?? "(stack unavailable)");
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.Take(8)) pending.Enqueue(inner);
            }
            else if (current.InnerException is { } inner)
                pending.Enqueue(inner);
        }
        if (suppressed > 0) text.Append($"Repeated stacks suppressed at this site: {suppressed}.");
        return text.ToString().TrimEnd();
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Content.Server._DeepLagoon.Apartments;

public sealed partial class ApartmentSystem
{
    /// <summary>One capture, bounded stage samples; contains no entities or serializer context.</summary>
    private sealed class SnapshotCaptureDiagnostics : IDisposable
    {
        private readonly (string Name, double Ms, long Bytes, double GcMs)[] _stages = new (string, double, long, double)[8];
        private int _count;
        private string? _stage;
        private long _started;
        private long _allocated;
        private TimeSpan _pause;

        public void Stage(string stage)
        {
            Finish();
            _stage = stage;
            _allocated = GC.GetAllocatedBytesForCurrentThread();
            _pause = GC.GetTotalPauseDuration();
            _started = Stopwatch.GetTimestamp();
        }

        private void Finish()
        {
            if (_stage == null) return;
            if (_count < _stages.Length)
                _stages[_count++] = (_stage, Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                    Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - _allocated),
                    (GC.GetTotalPauseDuration() - _pause).TotalMilliseconds);
            _stage = null;
        }

        public void Dispose() => Finish();

        public string Describe()
        {
            var text = new StringBuilder(512);
            for (var i = 0; i < _count; i++)
            {
                if (i > 0) text.Append(' ');
                var (name, ms, bytes, gc) = _stages[i];
                text.Append(CultureInfo.InvariantCulture, $"{name}_ms={ms:F3} {name}_thread_bytes={bytes} {name}_gc_pause_ms={gc:F3}");
            }
            return text.ToString();
        }
    }
}

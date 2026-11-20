using DiagnosticStopwatch = System.Diagnostics.Stopwatch;
using System.Text;

namespace Content.Server.Explosion.EntitySystems;

public sealed partial class ExplosionSystem
{
    internal enum ConsequencePhase : byte
    {
        EntityLookup,
        ResistanceAndContents,
        Damage,
        IgniteAndThrow,
        AtmosphereAndFire,
        FloorDamage,
        TileCommit,
        GridSplit,
        Count,
    }

    private readonly long[] _consequenceTotals = new long[(int) ConsequencePhase.Count];
    private readonly long[] _consequenceMaxima = new long[(int) ConsequencePhase.Count];
    private readonly int[] _consequenceCalls = new int[(int) ConsequencePhase.Count];
    private readonly string?[] _consequenceSubjects = new string?[(int) ConsequencePhase.Count];
    private bool _measureConsequences;
    private long _nextConsequenceReport;

    // A value type scope: no allocations or clock reads when diagnostics are disabled.
    internal ConsequenceMeasurement MeasureConsequence(ConsequencePhase phase, EntityUid? subject = null)
        => _measureConsequences ? new(this, phase, subject) : default;

    internal readonly struct ConsequenceMeasurement : IDisposable
    {
        private readonly ExplosionSystem? _system;
        private readonly ConsequencePhase _phase;
        private readonly EntityUid? _subject;
        private readonly long _started;

        public ConsequenceMeasurement(ExplosionSystem system, ConsequencePhase phase, EntityUid? subject)
        {
            _system = system;
            _phase = phase;
            _subject = subject;
            _started = DiagnosticStopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_system == null)
                return;

            var elapsed = DiagnosticStopwatch.GetTimestamp() - _started;
            var index = (int) _phase;
            _system._consequenceTotals[index] += elapsed;
            _system._consequenceCalls[index]++;
            if (elapsed <= _system._consequenceMaxima[index])
                return;

            _system._consequenceMaxima[index] = elapsed;
            // Preserve the identity before a queued deletion removes its metadata. Format only
            // a new slow maximum, never every damaged entity or tile.
            _system._consequenceSubjects[index] = elapsed >= DiagnosticStopwatch.Frequency / 100 && _subject is { } uid
                ? _system.ToPrettyString(uid).ToString()
                : null;
        }
    }

    private void ReportConsequences()
    {
        if (!_measureConsequences || DiagnosticStopwatch.GetTimestamp() < _nextConsequenceReport)
            return;

        var message = new StringBuilder($"Explosion consequences (nested timings): tick={_gameTiming.CurTick.Value}");
        for (var i = 0; i < (int) ConsequencePhase.Count; i++)
        {
            message.Append($" {(ConsequencePhase) i}:total_ms={_consequenceTotals[i] * 1000.0 / DiagnosticStopwatch.Frequency:F2}");
            message.Append($",max_ms={_consequenceMaxima[i] * 1000.0 / DiagnosticStopwatch.Frequency:F2},calls={_consequenceCalls[i]}");
            if (_consequenceSubjects[i] is { } subject)
                message.Append($",subject=\"{subject}\"");
        }
        Log.Info(message.ToString());
        Array.Clear(_consequenceTotals);
        Array.Clear(_consequenceMaxima);
        Array.Clear(_consequenceCalls);
        Array.Clear(_consequenceSubjects);
        _nextConsequenceReport = DiagnosticStopwatch.GetTimestamp() + 30 * DiagnosticStopwatch.Frequency;
    }
}

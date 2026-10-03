using System.Linq;
using System.Globalization;

namespace Content.Client._DeepLagoon.AmbientOcclusion;

internal enum AmbientOcclusionProfileMode { Off, Walls, Mask, Silhouette, Legacy }

internal struct AmbientOcclusionFrameStats
{
    public double CpuMs, CollectMs, ContactMs, ComposeMs, UploadMs, SubmitMs;
    public double LookupMs, ProcessMs, SortMs;
    public int Candidates, MissKey, MissObjects, MissBlockers, MissTiles;
    public int ProcessedCandidates, IndexHits, IndexRebuilds;
    public int Objects, Blockers, LosTests, ContactSamples, Tiles, Commands, UploadBytes, Draws, CacheHits, Rebuilds;

    public void Add(in AmbientOcclusionFrameStats other)
    {
        CpuMs += other.CpuMs; CollectMs += other.CollectMs; ContactMs += other.ContactMs;
        ComposeMs += other.ComposeMs; UploadMs += other.UploadMs; SubmitMs += other.SubmitMs;
        Objects += other.Objects; Blockers += other.Blockers; LosTests += other.LosTests;
        ContactSamples += other.ContactSamples; Tiles += other.Tiles; Commands += other.Commands;
        UploadBytes += other.UploadBytes; Draws += other.Draws;
        CacheHits += other.CacheHits; Rebuilds += other.Rebuilds;
        LookupMs += other.LookupMs; ProcessMs += other.ProcessMs; SortMs += other.SortMs;
        Candidates += other.Candidates;
        ProcessedCandidates += other.ProcessedCandidates; IndexHits += other.IndexHits; IndexRebuilds += other.IndexRebuilds;
        MissKey += other.MissKey; MissObjects += other.MissObjects;
        MissBlockers += other.MissBlockers; MissTiles += other.MissTiles;
    }
}

/// <summary>Samples completed engine frames, with a warm-up after each mode transition. Never mutates preferences.</summary>
internal sealed class AmbientOcclusionProfiler(Action<string> output, double seconds, double now)
{
    private readonly List<(double FrameMs, AmbientOcclusionFrameStats Stats)> _samples = new();
    private double _start = now;
    public AmbientOcclusionProfileMode Mode { get; private set; } = AmbientOcclusionProfileMode.Off;
    public bool Finished { get; private set; }
    private AmbientOcclusionFrameStats _pending;

    public void Record(in AmbientOcclusionFrameStats stats) => _pending.Add(stats);

    public void Advance(double now, double frameMs)
    {
        var pending = _pending;
        _pending = default;
        if (Finished || now - _start < 2)
            return;
        if (frameMs > 0)
            _samples.Add((frameMs, pending));
        if (now - _start < 2 + seconds)
            return;

        Report();
        _samples.Clear();
        if (Mode == AmbientOcclusionProfileMode.Legacy)
        {
            Finished = true;
            output("AO profile complete. Your normal AO settings are active again. CPU figures exclude deferred renderer/GPU work.");
            return;
        }
        Mode++;
        _start = now;
        output($"AO profile: {Mode}, 2s warm-up then {seconds:0}s sampling.");
    }

    private void Report()
    {
        if (_samples.Count == 0)
        {
            output($"AO {Mode}: no completed frames.");
            return;
        }
        var frames = _samples.Select(x => x.FrameMs).Order().ToArray();
        var average = frames.Average();
        double Mean(Func<AmbientOcclusionFrameStats, double> selector) => _samples.Average(x => selector(x.Stats));
        var p95 = frames[Math.Min(frames.Length - 1, (int) Math.Ceiling(frames.Length * 0.95) - 1)];
        var p99 = frames[Math.Min(frames.Length - 1, (int) Math.Ceiling(frames.Length * 0.99) - 1)];
        output(string.Format(CultureInfo.InvariantCulture,
            "AO {0} stutter: p99={1:F3}ms, max={2:F3}ms, frames >33ms={3}, >50ms={4}; AO CPU max={5:F3}ms.",
            Mode, p99, frames[^1], frames.Count(f => f > 33), frames.Count(f => f > 50),
            _samples.Max(x => x.Stats.CpuMs)));
        // FormattableString is not allowed by this engine's content sandbox.
        output(string.Format(CultureInfo.InvariantCulture,
            "AO {0}: frames={1}, FPS={2:F1}, frame={3:F3}ms, p95={4:F3}ms; " +
            "AO CPU={5:F3}ms (collect={6:F3}, contacts+LOS={7:F3}, compose={8:F3}, upload={9:F3}, submit={10:F3}); " +
            "objects={11:F0}, blockers={12:F0}, LOS tests={13:F0}, samples={14:F0}, tiles={15:F0}, " +
            "commands={16:F0}, upload KiB={17:F1}, viewport draws={18:F1}, cache hits={19:F2}, rebuilds={20:F2}.",
            Mode, frames.Length, 1000 / average, average, p95,
            Mean(s => s.CpuMs), Mean(s => s.CollectMs), Mean(s => s.ContactMs),
            Mean(s => s.ComposeMs), Mean(s => s.UploadMs), Mean(s => s.SubmitMs),
            Mean(s => s.Objects), Mean(s => s.Blockers), Mean(s => s.LosTests),
            Mean(s => s.ContactSamples), Mean(s => s.Tiles), Mean(s => s.Commands),
            Mean(s => s.UploadBytes) / 1024, Mean(s => s.Draws), Mean(s => s.CacheHits), Mean(s => s.Rebuilds)));
        output(string.Format(CultureInfo.InvariantCulture,
            "AO {0} detail: lookup={1:F3}ms, process={2:F3}ms, sort={3:F3}ms, candidates={4:F0}; " +
            "cache misses/frame: key={5:F2}, objects={6:F2}, blockers={7:F2}, tiles={8:F2} (first differing group).",
            Mode, Mean(s => s.LookupMs), Mean(s => s.ProcessMs), Mean(s => s.SortMs), Mean(s => s.Candidates),
            Mean(s => s.MissKey), Mean(s => s.MissObjects), Mean(s => s.MissBlockers), Mean(s => s.MissTiles)));
        output(string.Format(CultureInfo.InvariantCulture,
            "AO {0} work: processed candidates={1:F0}, visibility index hits={2:F2}, index rebuilds={3:F2}.",
            Mode, Mean(s => s.ProcessedCandidates), Mean(s => s.IndexHits), Mean(s => s.IndexRebuilds)));
    }
}

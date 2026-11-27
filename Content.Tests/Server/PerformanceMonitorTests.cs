using System.Globalization;
using Content.Server._DeepLagoon.Diagnostics;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture, NonParallelizable]
public sealed class PerformanceMonitorTests
{
    [TestCase(100u, 250u, 5, 30)]
    [TestCase(100u, 200u, 5, 20)]
    [TestCase(100u, 100u, 5, 0)]
    [TestCase(100u, 250u, 10, 15)]
    [TestCase(250u, 100u, 5, 0)]
    [TestCase(100u, 250u, 0, 0)]
    public void TpsCountsSimulationTicksAcrossRealElapsedTime(uint previous, uint current, double seconds, double expected)
    {
        Assert.That(PerformanceMonitorSystem.MeasureTps(previous, current, seconds), Is.EqualTo(expected));
    }

    [Test]
    public void DetailedSystemsDistinguishUnmeasuredAllocationsFromMeasuredZeroAndPvsSamplesStaySamples()
    {
        const string line = "Performance system: name=PhysicsSystem avg_ms_per_tick=3.5 max_call_ms=8 max_tick_ms=12 calls=400 allocated_main_thread_bytes=0 failed_calls=0 ";
        var summary = PerformanceMonitorSystem.ParseDetailedSystem(line + "allocations_and_failures_measured=False");
        Assert.That(summary, Is.Not.Null);
        Assert.That(summary!.Calls, Is.EqualTo(400));
        Assert.That(summary.MaximumTickMs, Is.EqualTo(12));
        Assert.That(summary.AllocatedBytes, Is.Null);
        Assert.That(summary.FailedCalls, Is.Null);
        Assert.That(PerformanceMonitorSystem.ParseDetailedSystem(line.Replace("calls=400 ", "") + "allocations_and_failures_measured=False")!.Calls, Is.EqualTo(-1));
        var detailed = PerformanceMonitorSystem.ParseDetailedSystem(line + "allocations_and_failures_measured=True");
        Assert.That(detailed!.AllocatedBytes, Is.Zero);
        Assert.That(detailed.FailedCalls, Is.Zero);
        var component = PerformanceMonitorSystem.ParsePvsComponent("Performance PVS component sample: name=Transform samples=40 sampled_total_ms=3.2 sampled_avg_ms=0.08 sampled_max_ms=1.3 worst_entity_uid=71 statistical_estimate=False");
        Assert.That(component!.Samples, Is.EqualTo(40));
        Assert.That(component.TotalMs, Is.EqualTo(3.2));
        Assert.That(component.AverageMs, Is.EqualTo(.08));
        Assert.That(component.WorstEntity, Is.EqualTo("71"));
        Assert.That(PerformanceMonitorSystem.ParsePvsComponent("name=Invalid samples=0 sampled_total_ms=NaN"), Is.Null);
    }
    [Test]
    public void EngineSummaryUsesInvariantMeasurementsAndIdentifiesPhysicsRatherThanTickInterval()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var values = PerformanceMonitorSystem.ParseSummary(
                "Server tick timing summary: window_s=30.0 tps=20.0 tick_avg_ms=12.50 simulation_avg_ms=8.25 pvs_prepare_avg_ms=1.50 system1=SharedPhysicsSystem system1_avg_ms=3.75 system1_max_ms=7.0");
            Assert.That(values["tick_avg_ms"], Is.EqualTo(12.5));
            Assert.That(values["simulation_avg_ms"], Is.EqualTo(8.25));
            Assert.That(values["physics1_ms"], Is.EqualTo(3.75));
            Assert.That(values["tick_avg_ms"], Is.Not.EqualTo(1000 / values["tps"]));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Test]
    public void UnavailableOrCorruptMetricsStayUnavailableInsteadOfBecomingZero()
    {
        var values = PerformanceMonitorSystem.ParseSummary(
            "Server tick timing summary: window_s=60.0 tick_avg_ms=NaN simulation_avg_ms=bad system1=PhysicsSystem system1_avg_ms=Infinity physics_ms=-4");
        Assert.That(values.ContainsKey("tick_avg_ms"), Is.False);
        Assert.That(values.ContainsKey("simulation_avg_ms"), Is.False);
        Assert.That(values.ContainsKey("physics1_ms"), Is.False);
        Assert.That(values.ContainsKey("physics_ms"), Is.False);
        Assert.That(values["window_s"], Is.EqualTo(60));
        Assert.That(PerformanceMonitorSystem.ParseSummary(new string('x', 32769)), Is.Empty);
    }

    [Test]
    public void InclusivePvsDispatchDoesNotAddItsChildrenAndStaleSummaryIsNotReused()
    {
        var summary = PerformanceMonitorSystem.ParseSummary(
            "Server tick timing summary: window_s=60 tick_avg_ms=20 simulation_avg_ms=12 pvs_dispatch_avg_ms=7 pvs_prepare_avg_ms=3 pvs_serialize_avg_ms=4");
        var actual = PerformanceMonitorSystem.ReadPhaseDurations(summary, fresh: true);
        Assert.That(actual.GameState, Is.EqualTo(7));
        Assert.That(actual.Physics, Is.Null, "An absent physics measurement is unavailable, not a measured zero.");
        var stale = PerformanceMonitorSystem.ReadPhaseDurations(summary, fresh: false);
        Assert.That(stale.Tick, Is.Null);
        Assert.That(stale.Simulation, Is.Null);
        Assert.That(stale.GameState, Is.Null);
    }

    [Test]
    public void SystemRankingKeepsMeasuredUnitsAndBoundsUntrustedFields()
    {
        var rows = PerformanceMonitorSystem.ParseSystems("Server tick timing summary: system1=PhysicsSystem system1_avg_ms=3.5 system1_max_ms=8 system1_calls=420 " +
            "system2=PathfindingSystem system2_avg_ms=7 system2_max_ms=9 system3=Invalid system3_avg_ms=NaN");
        Assert.That(rows, Has.Length.EqualTo(2));
        Assert.That(rows[0].Name, Is.EqualTo("PathfindingSystem"));
        Assert.That(rows[0].AverageMs, Is.EqualTo(7));
        Assert.That(rows[1].MaximumMs, Is.EqualTo(8));
        Assert.That(rows[1].Calls, Is.EqualTo(420));
        Assert.That(rows[0].Calls, Is.EqualTo(-1));
        Assert.That(PerformanceMonitorSystem.ParseSystems(new string('x', 32769)), Is.Empty);
        var memory = PerformanceMonitorSystem.ParseSummary("Memory diagnostic summary: last_gc_index=12 loh_after_bytes=16777216 ecs.entities=4080 capture_main_ms=0.25");
        Assert.That(memory["loh_after_bytes"], Is.EqualTo(16777216));
        Assert.That(memory["ecs.entities"], Is.EqualTo(4080));
    }
}

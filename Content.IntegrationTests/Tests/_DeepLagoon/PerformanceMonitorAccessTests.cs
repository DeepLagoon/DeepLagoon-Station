using System;
using System.Linq;
using System.Reflection;
using Content.Server._DeepLagoon.Diagnostics;
using Content.Server.Administration.Managers;
using Content.Server.Database;
using Content.Shared.CCVar;
using Content.Shared._DeepLagoon.Performance;
using Moq;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;
using Robust.Shared.Log;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class PerformanceMonitorAccessTests
{
    [Test]
    public async Task NormalPlayerCannotSubscribeAndDeadminClosesMonitorWithoutReplacingOtherCallbacks()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server;
        var entities = server.EntMan;
        var admins = server.ResolveDependency<IAdminManager>();
        var database = server.ResolveDependency<IServerDbManager>();
        var configuration = server.ResolveDependency<IConfigurationManager>();
        PerformanceMonitorSystem monitor = null!;
        var actualClock = server.ResolveDependency<IGameTiming>();
        var sampleTime = actualClock.RealTime;
        var sampleClock = new Mock<IGameTiming>();
        sampleClock.SetupGet(clock => clock.RealTime).Returns(() => sampleTime);
        sampleClock.SetupGet(clock => clock.CurTime).Returns(() => actualClock.CurTime);
        sampleClock.SetupGet(clock => clock.FramesPerSecondAvg).Returns(() => actualClock.FramesPerSecondAvg);
        var clockField = typeof(PerformanceMonitorSystem).GetField("_clock", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalDetail = 0;
        Action<string, long, int> sentinel = (_, _, _) => { };
        await server.WaitPost(() =>
        {
            originalDetail = configuration.GetCVar(Robust.Shared.CVars.PerformanceLogLevel);
            configuration.SetCVar(CCVars.ConsoleLoginLocal, false);
            admins.ReloadAdmin(pair.Player!);
            monitor = entities.System<PerformanceMonitorSystem>();
        });
        await PoolManager.WaitUntil(server, () => !monitor.CanRead(pair.Player!));
        await server.WaitAssertion(() =>
        {
            Assert.That(monitor.Open(pair.Player!), Is.False);
            Assert.That(monitor.ViewerCount, Is.Zero);
            monitor.Update(10);
            Assert.That(monitor.HistoryCount, Is.GreaterThanOrEqualTo(0), "Read permissions do not disable background recording.");
        });
        await database.AddAdminAsync(new Admin { UserId = pair.Player!.UserId.UserId,
            Flags = new() { new AdminFlag { Flag = "DEBUG" } } });
        await server.WaitPost(() => admins.ReloadAdmin(pair.Player!));
        await PoolManager.WaitUntil(server, () => monitor.CanRead(pair.Player!));
        await server.WaitAssertion(() =>
        {
            Assert.That(monitor.CanConfigure(pair.Player!), Is.False);
            Assert.That(monitor.Configure(pair.Player!, new(PerformanceDiagnosticAction.PhasesDetailed)), Does.Contain("Host"));
            Assert.That(configuration.GetCVar(Robust.Shared.CVars.PerformanceLogLevel), Is.EqualTo(originalDetail));
        });
        try
        {
            await server.WaitAssertion(() =>
            {
                // Integration ticks advance simulation without waiting for real wall-clock seconds.
                // Control only the monitor's scheduler clock; still exercise its real Update/Sample path.
                clockField.SetValue(monitor, sampleClock.Object);
                configuration.SetCVar(PerformanceMonitorCVars.SampleInterval, 1f);
                typeof(PerformanceMonitorSystem).GetField("_previousSample", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(monitor, sampleTime.TotalSeconds);
                typeof(PerformanceMonitorSystem).GetField("_round", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(monitor, monitor.CurrentRound);
                ((System.Collections.Generic.Queue<PerformanceMonitorSample>)typeof(PerformanceMonitorSystem).GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(monitor)!).Clear();
                configuration.SetCVar(Robust.Shared.CVars.PerformanceLogLevel, 1);
                entities.DiagnosticWorkTiming += sentinel;
                Assert.That(monitor.Open(pair.Player!), Is.True);
                Assert.That(monitor.ViewerCount, Is.EqualTo(1));
                Assert.That(entities.DiagnosticWorkTiming!.GetInvocationList(), Does.Contain(sentinel));
                entities.DiagnosticWorkTiming?.Invoke("npc.pathfinding.reopening_test", 100, 1);
                server.ResolveDependency<ILogManager>().GetSawmill("srv.memory").Info(
                    "Memory diagnostic summary: last_gc_index=4 loh_after_bytes=16777216 ecs.entities=4080");
                sampleTime += TimeSpan.FromSeconds(.5);
                monitor.Update(1);
                Assert.That(monitor.HistoryCount, Is.Zero, "No sample before the real-time sampling deadline.");
                sampleTime += TimeSpan.FromSeconds(.75);
                monitor.Update(1);
                var state = monitor.State(-1, "");
                Assert.That(state.Samples, Has.Length.EqualTo(1));
                Assert.That(state.Samples[0].WindowSeconds, Is.EqualTo(1.25).Within(.000001),
                    "Rates must use actual elapsed sampler time, not the simulated Update argument.");
                Assert.That(state.Samples[0].ManagedMiB, Is.GreaterThan(0));
                Assert.That(state.Samples[0].WorkingSetMiB, Is.GreaterThan(0));
                Assert.That(state.Phases.Any(phase => phase.Name == "npc.pathfinding.reopening_test"), Is.True);
                Assert.That(state.Memory.Single(metric => metric.Name == "loh_after_bytes").Value, Is.EqualTo(16777216));
                Assert.That(state.MemorySequence, Is.Positive);
                Assert.That(monitor.State(-1, "", pair.Player, state.MemorySequence).Memory, Is.Empty,
                    "Unchanged subsystem counters must not be retransmitted every two seconds.");
            });
            await server.WaitAssertion(() =>
            {
                admins.DeAdmin(pair.Player!);
                Assert.That(monitor.ViewerCount, Is.Zero);
                Assert.That(entities.DiagnosticWorkTiming!.GetInvocationList(), Does.Contain(sentinel),
                    "Closing the monitor must leave other diagnostic consumers subscribed.");
                Assert.That(monitor.Open(pair.Player!), Is.False);
                var history = monitor.HistoryCount;
                sampleTime += TimeSpan.FromSeconds(10);
                monitor.Update(10);
                Assert.That(monitor.HistoryCount, Is.GreaterThan(history), "The round journal must continue recording with no windows open.");
                Assert.That(monitor.State(-1, "").Phases, Is.Empty);
                admins.ReAdmin(pair.Player!);
                Assert.That(monitor.Open(pair.Player!), Is.True);
                Assert.That(monitor.HistoryCount, Is.GreaterThan(0), "Reopening must retain the server's already recorded history.");
                Assert.That(monitor.State(-1, "").Phases, Is.Empty, "Reopening must not show detail measurements from the previous subscription.");
                Assert.That(monitor.State(-1, "").Memory.Any(metric => metric.Name == "loh_after_bytes"), Is.True);
                admins.DeAdmin(pair.Player!);
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                entities.DiagnosticWorkTiming -= sentinel;
                configuration.SetCVar(Robust.Shared.CVars.PerformanceLogLevel, originalDetail);
                configuration.SetCVar(PerformanceMonitorCVars.SampleInterval, 5f);
                clockField.SetValue(monitor, actualClock);
            });
        }
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HostControlsUseWhitelistedActionsAndDoNotResetSettingsWhenMonitorCloses()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server;
        var configuration = server.ResolveDependency<IConfigurationManager>();
        var admins = server.ResolveDependency<IAdminManager>();
        PerformanceMonitorSystem monitor = null!;
        var clockField = typeof(PerformanceMonitorSystem).GetField("_clock", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var actualClock = server.ResolveDependency<IGameTiming>();
        var now = actualClock.RealTime;
        var clock = new Mock<IGameTiming>();
        clock.SetupGet(value => value.RealTime).Returns(() => now);
        clock.SetupGet(value => value.CurTime).Returns(() => actualClock.CurTime);
        var original = configuration.GetCVar(Robust.Shared.CVars.PerformanceLogLevel);
        var originalInterval = configuration.GetCVar(Robust.Shared.CVars.PerformanceLogInterval);
        int? originalMemory = null, originalAutoTrace = null;
        await server.WaitPost(() => { monitor = server.EntMan.System<PerformanceMonitorSystem>(); admins.PromoteHost(pair.Player!); });
        await PoolManager.WaitUntil(server, () => monitor.CanConfigure(pair.Player!));
        try
        {
            await server.WaitAssertion(() =>
            {
                clockField.SetValue(monitor, clock.Object);
                Assert.That(monitor.Configure(pair.Player!, new((PerformanceDiagnosticAction)255)), Does.Contain("Неизвестное"));
                Assert.That(monitor.Configure(pair.Player!, new(PerformanceDiagnosticAction.PhasesDetailed)), Does.Contain("2"));
                Assert.That(configuration.GetCVar(Robust.Shared.CVars.PerformanceLogLevel), Is.EqualTo(2));
                Assert.That(monitor.Configure(pair.Player!, new(PerformanceDiagnosticAction.PhasesOff)), Does.Contain("секунду"));
                now += TimeSpan.FromSeconds(2);
                monitor.Configure(pair.Player!, new(PerformanceDiagnosticAction.PhaseInterval10));
                Assert.That(configuration.GetCVar(Robust.Shared.CVars.PerformanceLogInterval), Is.EqualTo(10));
                Assert.That(monitor.State(-1, "", pair.Player).Controls.CanConfigure, Is.True);
                Assert.That(monitor.Open(pair.Player!), Is.True);
                admins.DeAdmin(pair.Player!);
                Assert.That(monitor.ViewerCount, Is.Zero);
                Assert.That(configuration.GetCVar(Robust.Shared.CVars.PerformanceLogLevel), Is.EqualTo(2), "Closing a viewer does not restore another operator's global settings.");
                now += TimeSpan.FromSeconds(2);
                Assert.That(monitor.Configure(pair.Player!, new(PerformanceDiagnosticAction.PhasesOff)), Does.Contain("Host"));
                Assert.That(configuration.GetCVar(Robust.Shared.CVars.PerformanceLogLevel), Is.EqualTo(2));
                admins.ReAdmin(pair.Player!);
                var state = monitor.State(-1, "", pair.Player);
                if (Environment.GetEnvironmentVariable("DEEPLAGOON_EXPECT_MEMORY_DIAGNOSTICS") == "1")
                    Assert.That(state.Controls.MemoryAvailable, Is.True, "The fork run must exercise the enabled memory bridge.");
                if (!state.Controls.MemoryAvailable)
                    Assert.That(monitor.Configure(pair.Player!, new(PerformanceDiagnosticAction.HeapSnapshot, true)), Does.Contain("недоступна"));
                else
                {
                    originalMemory = (int)configuration.GetCVar("log.memory_diagnostics");
                    originalAutoTrace = (int)configuration.GetCVar("log.memory_trace_interval");
                    now += TimeSpan.FromSeconds(2);
                    monitor.Configure(pair.Player!, new(PerformanceDiagnosticAction.AutoTraceOff));
                    Assert.That(configuration.GetCVar("log.memory_trace_interval"), Is.EqualTo(0));
                    now += TimeSpan.FromSeconds(2);
                    monitor.Configure(pair.Player!, new(PerformanceDiagnosticAction.MemoryTraces));
                    Assert.That(configuration.GetCVar("log.memory_diagnostics"), Is.EqualTo(2));
                    now += TimeSpan.FromSeconds(2);
                    monitor.Configure(pair.Player!, new(PerformanceDiagnosticAction.MemorySnapshots));
                    Assert.That(configuration.GetCVar("log.memory_diagnostics"), Is.EqualTo(3));
                    now += TimeSpan.FromSeconds(2);
                    Assert.That(monitor.Configure(pair.Player!, new(PerformanceDiagnosticAction.HeapSnapshot)), Does.Contain("Подтвердите"));
                }
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                configuration.SetCVar(Robust.Shared.CVars.PerformanceLogLevel, original);
                configuration.SetCVar(Robust.Shared.CVars.PerformanceLogInterval, originalInterval);
                if (originalMemory is { } memory) configuration.SetCVar("log.memory_diagnostics", memory);
                if (originalAutoTrace is { } autoTrace) configuration.SetCVar("log.memory_trace_interval", autoTrace);
                clockField.SetValue(monitor, actualClock);
            });
        }
        await pair.CleanReturnAsync();
    }
}

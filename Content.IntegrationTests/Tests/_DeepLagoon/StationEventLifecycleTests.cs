#nullable enable annotations
using System;
using System.Reflection;
using Content.Server.GameTicking;
using Content.Server.StationEvents.Components;
using Content.Server.StationEvents.Events;
using Content.Shared.Destructible.Thresholds;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
using Moq;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class StationEventLifecycleTests
{
    private const string BareRule = "DLTestBareStationEventLifecycle";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestPrototypes]
    private const string Prototypes = """
        - type: entity
          id: DLTestBareStationEventLifecycle
          components:
          - type: GameRule
          - type: StationEvent
        """;

    // Like the existing RoundEndTestSystem, this shared-event observer is discovered
    // with the test assembly and subscribes during Initialize, before the event-bus lock.
    private sealed class LifecycleObserverSystem : EntitySystem
    {
        public EntityUid First, Ending;
        public int Starts, Ends;
        public bool Reentered, RepeatedEndAccepted;

        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<GameRuleStartedEvent>(OnStarted);
            SubscribeLocalEvent<GameRuleEndedEvent>(OnEnded);
        }

        private void OnStarted(ref GameRuleStartedEvent args)
        {
            if (args.RuleEntity == First || args.RuleEntity == Ending)
                Starts++;
        }

        private void OnEnded(ref GameRuleEndedEvent args)
        {
            if (args.RuleEntity != Ending)
                return;
            Ends++;
            EntityManager.System<StationEventLifecycleSystem>().ProcessEvents();
            EntityManager.System<SolarFlareRule>().Update(0);
            RepeatedEndAccepted = EntityManager.System<GameTicker>().EndGameRule(Ending);
            Reentered = true;
        }
    }

    private sealed class Clock : IDisposable
    {
        private readonly StationEventLifecycleSystem _system;
        private readonly FieldInfo _field;
        private readonly object? _previous;
        public GameTick Tick;
        public TimeSpan Now;

        public Clock(StationEventLifecycleSystem system, uint tick, TimeSpan now)
        {
            _system = system;
            Tick = new GameTick(tick);
            Now = now;
            var mock = new Mock<IGameTiming>();
            mock.SetupGet(timing => timing.CurTick).Returns(() => Tick);
            mock.SetupGet(timing => timing.CurTime).Returns(() => Now);
            _field = typeof(StationEventLifecycleSystem).GetField("_timing", Private)!;
            _previous = _field.GetValue(system);
            _field.SetValue(system, mock.Object);
            typeof(StationEventLifecycleSystem).GetField("_lastProcessedTick", Private)!.SetValue(system, null);
        }

        public void Dispose()
        {
            _field.SetValue(_system, _previous);
            typeof(StationEventLifecycleSystem).GetField("_lastProcessedTick", Private)!.SetValue(_system, null);
        }
    }

    [Test]
    public async Task BareEventsShareOnePassAcrossDerivedCallersAndReentrantEndAtTickWraparound()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var em = pair.Server.EntMan;
        await pair.Server.WaitAssertion(() =>
        {
            var ticker = em.System<GameTicker>();
            var lifecycle = em.System<StationEventLifecycleSystem>();
            var firstCaller = em.System<FalseAlarmRule>();
            var otherCaller = em.System<SolarFlareRule>();
            using var clock = new Clock(lifecycle, uint.MaxValue, TimeSpan.FromMinutes(10));
            var first = ticker.AddGameRule(BareRule);
            var ending = ticker.AddGameRule(BareRule);
            var firstEvent = em.GetComponent<StationEventComponent>(first);
            var endingEvent = em.GetComponent<StationEventComponent>(ending);
            var observer = em.System<LifecycleObserverSystem>();
            observer.First = first;
            observer.Ending = ending;
            EntityUid late = default;
            try
            {
                // Neither event has FalseAlarm/SolarFlare (or any other derived rule) components.
                firstCaller.Update(0);
                Assert.That(ticker.IsGameRuleActive(first), Is.True);
                Assert.That(ticker.IsGameRuleActive(ending), Is.True);
                Assert.That(observer.Starts, Is.EqualTo(2));
                firstEvent.EndTime = clock.Now + TimeSpan.FromSeconds(10);
                firstEvent.WarningDurationLeft = 15;
                endingEvent.EndTime = clock.Now + TimeSpan.FromSeconds(2);
                endingEvent.WarningDurationLeft = 0;

                // A wrapped tick must still process warnings; a second derived caller
                // in that same tick must not start newly-added events in another pass.
                clock.Tick = new GameTick(0);
                firstCaller.Update(0);
                Assert.That(firstEvent.WarningAnnounced, Is.True);
                Assert.That(endingEvent.WarningAnnounced, Is.False);
                late = ticker.AddGameRule(BareRule);
                otherCaller.Update(0);
                lifecycle.ProcessEvents();
                Assert.That(ticker.IsGameRuleActive(late), Is.False);

                clock.Tick = new GameTick(1);
                clock.Now += TimeSpan.FromSeconds(3);
                otherCaller.Update(0);
                Assert.That(ticker.IsGameRuleActive(late), Is.True);
                Assert.That(ticker.IsGameRuleActive(ending), Is.False);
                Assert.That(em.HasComponent<EndedGameRuleComponent>(ending), Is.True);
                Assert.That(observer.Ends, Is.EqualTo(1));
                Assert.That(firstEvent.WarningAnnounced, Is.True);
                Assert.That(observer.Reentered, Is.True);
                Assert.That(observer.RepeatedEndAccepted, Is.False);

                // Ended history remains present but must never warn/restart on later passes.
                clock.Tick = new GameTick(2);
                clock.Now += TimeSpan.FromSeconds(1);
                firstCaller.Update(0);
                otherCaller.Update(0);
                Assert.That(endingEvent.WarningAnnounced, Is.False);
                Assert.That(ticker.IsGameRuleActive(ending), Is.False);
                Assert.That(observer.Starts, Is.EqualTo(2));
                Assert.That(observer.Ends, Is.EqualTo(1));
            }
            finally
            {
                observer.First = default;
                observer.Ending = default;
                em.DeleteEntity(first);
                em.DeleteEntity(ending);
                if (late.IsValid())
                    em.DeleteEntity(late);
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DelayedBareEventWaitsForTheNativeDelayedStartScheduler()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true, DummyTicker = false });
        var em = pair.Server.EntMan;
        EntityUid uid = default;
        TimeSpan deadline = default;
        await pair.Server.WaitAssertion(() =>
        {
            var lifecycle = em.System<StationEventLifecycleSystem>();
            using var clock = new Clock(lifecycle, 0, pair.Server.ResolveDependency<IGameTiming>().CurTime);
            uid = em.System<GameTicker>().AddGameRule(BareRule);
            em.GetComponent<GameRuleComponent>(uid).Delay = new MinMax(1, 1);
            lifecycle.ProcessEvents();
            Assert.That(em.HasComponent<DelayedStartRuleComponent>(uid), Is.True);
            Assert.That(em.System<GameTicker>().IsGameRuleActive(uid), Is.False);
            deadline = em.GetComponent<DelayedStartRuleComponent>(uid).RuleStartTime;

            clock.Tick = new GameTick(1);
            clock.Now = deadline + TimeSpan.FromSeconds(5);
            lifecycle.ProcessEvents();
            Assert.That(em.System<GameTicker>().IsGameRuleActive(uid), Is.False,
                "The shared pass must leave the delayed marker to the native scheduler, rather than bypassing it.");
            Assert.That(em.GetComponent<DelayedStartRuleComponent>(uid).RuleStartTime, Is.EqualTo(deadline));
        });
        var ticks = (int) pair.Server.ResolveDependency<IGameTiming>().TickRate + 3;
        await pair.Server.WaitRunTicks(ticks);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(pair.Server.ResolveDependency<IGameTiming>().CurTime, Is.GreaterThanOrEqualTo(deadline));
            Assert.That(em.System<GameTicker>().IsGameRuleActive(uid), Is.True);
            Assert.That(em.HasComponent<DelayedStartRuleComponent>(uid), Is.False);
            em.DeleteEntity(uid);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RoundCleanupAllowsTheFirstPassEvenWhenTheTickHasNotChanged()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var em = pair.Server.EntMan;
        await pair.Server.WaitAssertion(() =>
        {
            var lifecycle = em.System<StationEventLifecycleSystem>();
            var ticker = em.System<GameTicker>();
            using var clock = new Clock(lifecycle, 0, pair.Server.ResolveDependency<IGameTiming>().CurTime);
            lifecycle.ProcessEvents();
            em.EventBus.RaiseEvent(EventSource.Local, new RoundRestartCleanupEvent());
            var uid = ticker.AddGameRule(BareRule);
            lifecycle.ProcessEvents();
            Assert.That(ticker.IsGameRuleActive(uid), Is.True);
            em.DeleteEntity(uid);
        });
        await pair.CleanReturnAsync();
    }
}

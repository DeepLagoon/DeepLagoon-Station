using System.Linq;
using System.Reflection;
using Content.Server._DeepLagoon.Apartments;
using Content.Server.Fluids.EntitySystems;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids;
using Content.Shared.Fluids.Components;
using Content.Shared.Timing;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentPuddlePersistenceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestCase(false)]
    [TestCase(true)]
    public async Task RestoredMopCanCleanRestoredPuddleAfterServerClockResets(bool activeCooldown)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var outside = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            pair.Server.CfgMan.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentSmall");
            var entities = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            var apartments = entities.System<ApartmentSystem>();
            var puddles = entities.System<PuddleSystem>();
            var solutions = entities.System<SharedSolutionContainerSystem>();
            var delays = entities.System<UseDelaySystem>();
            var timing = (GameTiming) pair.Server.ResolveDependency<IGameTiming>();
            var originalTimeBase = timing.TimeBase;
            EntityUid body = default;
            try
            {
                // Save late in one server process, then load at the beginning of
                // another. No world ticks run while the test shifts the time base.
                timing.TimeBase = (TimeSpan.FromHours(10), timing.CurTick);
                body = entities.SpawnEntity("MobHuman", outside.GridCoords);
                pair.Server.PlayerMan.SetAttachedEntity(session, body);
                Assert.That(apartments.Enter(session, session.UserId, out var message), Is.True, message);
                var home = apartments.Instances[session.UserId];
                var mop = entities.SpawnEntity("MopItem", new EntityCoordinates(home.Grid, 3.5f, 2.5f));
                Assert.That(solutions.TryGetSolution(mop, AbsorbentComponent.SolutionName, out var mopSolution), Is.True);
                Assert.That(solutions.TryAddSolution(mopSolution.Value, new Solution("Water", FixedPoint2.New(50))), Is.True);
                Assert.That(puddles.TrySpillAt(new EntityCoordinates(home.Grid, 1.5f, 2.5f),
                    new Solution("Blood", FixedPoint2.New(5)), out var firstPuddle, sound: false), Is.True);
                entities.System<AbsorbentSystem>().Mop(body, firstPuddle, mop, entities.GetComponent<AbsorbentComponent>(mop));
                Assert.That(delays.IsDelayed(mop), Is.True);
                if (!activeCooldown)
                    timing.TimeBase = (TimeSpan.FromHours(10) + TimeSpan.FromSeconds(2), timing.CurTick);
                Assert.That(delays.IsDelayed(mop), Is.EqualTo(activeCooldown));
                Assert.That(puddles.TrySpillAt(new EntityCoordinates(home.Grid, 3.5f, 3.5f),
                    new Solution("Blood", FixedPoint2.New(5)), out _, sound: false), Is.True);
                Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
                Unload(apartments, entities, pair.Server.ResolveDependency<ITaskManager>(), home);

                timing.TimeBase = originalTimeBase;
                Assert.That(apartments.Enter(session, session.UserId, out message), Is.True, message);
                var restored = apartments.Instances[session.UserId];
                var restoredMop = entities.EntityQuery<AbsorbentComponent, TransformComponent>()
                    .Single(entry => entry.Item2.GridUid == restored.Grid).Item2.Owner;
                var restoredPuddle = entities.EntityQuery<PuddleComponent, TransformComponent>()
                    .Where(entry => entry.Item2.GridUid == restored.Grid)
                    .Select(entry => entry.Item2.Owner).Single(uid =>
                        solutions.TryGetSolution(uid, "puddle", out _, out var solution) && solution.ContainsPrototype("Blood"));
                Assert.That(delays.IsDelayed(restoredMop), Is.EqualTo(activeCooldown),
                    "A saved mop cooldown must preserve its remaining duration across a server-clock reset.");
                Assert.That(delays.TryGetDelayInfo(restoredMop, out var cooldown), Is.True);
                Assert.That((cooldown!.EndTime - timing.CurTime).TotalSeconds, Is.InRange(-1.01, 1.01));
                if (activeCooldown)
                    timing.TimeBase = (timing.CurTime + TimeSpan.FromSeconds(2), timing.CurTick);
                Assert.That(delays.IsDelayed(restoredMop), Is.False);
                entities.System<AbsorbentSystem>().Mop(body, restoredPuddle, restoredMop,
                    entities.GetComponent<AbsorbentComponent>(restoredMop));
                Assert.That(solutions.TryGetSolution(restoredPuddle, "puddle", out _, out var remaining), Is.True);
                Assert.That(remaining.GetTotalPrototypeQuantity("Blood"), Is.EqualTo(FixedPoint2.Zero));
                Assert.That(apartments.Leave(session.UserId, out _), Is.True);
            }
            finally
            {
                timing.TimeBase = originalTimeBase;
                pair.Server.PlayerMan.SetAttachedEntity(session, null);
                if (entities.EntityExists(body)) entities.DeleteEntity(body);
            }
        });
        await pair.CleanReturnAsync();
    }

    private static void Unload(ApartmentSystem apartments, IEntityManager entities, ITaskManager tasks, ApartmentSystem.Instance home)
    {
        home.IdleSince = TimeSpan.FromSeconds(-10000);
        typeof(ApartmentSystem).GetField("_nextUpdate", Private)!.SetValue(apartments, TimeSpan.Zero);
        apartments.Update(1);
        tasks.BlockWaitOnTask((Task) typeof(ApartmentSystem).GetField("_maintenanceSave", Private)!.GetValue(apartments)!);
        var deletion = entities.System<Content.Server._DeepLagoon.Performance.StagedMapDeletionSystem>();
        for (var i = 0; i < 1000 && deletion.PendingCount > 0; i++) deletion.Update(0);
        Assert.That(entities.EntityExists(home.Grid), Is.False);
    }
}

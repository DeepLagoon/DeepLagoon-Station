using Content.Server.Shuttles.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;

namespace Content.IntegrationTests.Tests.Shuttle;

[TestFixture]
public sealed class FtlTransitCollisionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task TransitHullIsolationEndsWhenTheShuttleLeavesHyperspace(bool reverse)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var transit = await pair.CreateTestMap();
        var destination = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var transforms = entities.System<SharedTransformSystem>();
            entities.AddComponent<FTLMapComponent>(transit.MapUid);
            var other = maps.CreateGridEntity(transit.MapId);

            bool Prevented(EntityUid a, EntityUid b)
            {
                var ev = new PreventCollideEvent(a, b,
                    entities.GetComponent<PhysicsComponent>(a),
                    entities.GetComponent<PhysicsComponent>(b), null!, null!);
                entities.EventBus.RaiseLocalEvent(a, ref ev);
                return ev.Cancelled;
            }

            // Independent journeys (including hull fragments without an FTL component)
            // must never get as far as the solver or shuttle impact damage handler.
            Assert.That(reverse ? Prevented(other, transit.Grid) : Prevented(transit.Grid, other), Is.True);

            // Do not disable physics between the hull and loose objects on board.
            var loose = entities.SpawnEntity(null, new Robust.Shared.Map.EntityCoordinates(transit.Grid, 0, 0));
            entities.AddComponent<PhysicsComponent>(loose);
            Assert.That(Prevented(transit.Grid, loose), Is.False);

            // Arrival restores normal hull collisions immediately, even when the
            // shuttle still has an FTL component for its cooldown.
            entities.EnsureComponent<FTLComponent>(transit.Grid).State = FTLState.Cooldown;
            transforms.SetParent(transit.Grid, destination.MapUid);
            transforms.SetParent(other, destination.MapUid);
            Assert.That(reverse ? Prevented(other, transit.Grid) : Prevented(transit.Grid, other), Is.False);
        });
        await pair.CleanReturnAsync();
    }
}

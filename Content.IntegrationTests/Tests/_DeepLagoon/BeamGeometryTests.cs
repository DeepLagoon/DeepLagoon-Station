using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Beam;
using Content.Server.Beam.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class BeamGeometryTests
{
    private sealed class RejectedFixtureListener : EntitySystem
    {
        public readonly List<InvalidPhysicsFixtureEvent> Rejected = new();

        public override void Initialize()
            => SubscribeLocalEvent<InvalidPhysicsFixtureEvent>(OnRejected);

        private void OnRejected(ref InvalidPhysicsFixtureEvent ev) => Rejected.Add(ev);
    }

    [Test]
    public async Task ShortAndUnitLightningBeamsHaveFiniteNonzeroFixtures()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var maps = em.System<SharedMapSystem>();
            var transform = em.System<SharedTransformSystem>();
            var beams = em.System<BeamSystem>();
            var listener = em.System<RejectedFixtureListener>();
            var map = maps.CreateMap(out _);
            // Adjacent tile centers, diagonal targets, short distances and an ordinary long beam.
            foreach (var origin in new[] { Vector2.Zero, new Vector2(8631.5f, 24871.25f) })
            foreach (var delta in new[] { Vector2.UnitX, Vector2.UnitY, new Vector2(1, 1),
                         new Vector2(.5f, 0), new Vector2(1.0001f, 0), new Vector2(4, 0) })
            {
                var source = em.SpawnEntity(null, new EntityCoordinates(map, origin));
                var target = em.SpawnEntity(null, new EntityCoordinates(map, origin + delta));
                var before = BeamFixtures().Select(x => x.Uid).ToHashSet();
                beams.TryCreateBeam(source, target, "HyperchargedLightning");
                Assert.That(listener.Rejected, Is.Empty, $"Rejected beam at {origin} + {delta}.");
                var created = BeamFixtures().Where(x => !before.Contains(x.Uid)).ToArray();
                Assert.That(created, Has.Length.EqualTo(1));
                var (uid, edge) = created.Single();
                Assert.That(em.IsQueuedForDeletion(uid), Is.False);
                var position = transform.GetWorldPosition(uid);
                var bounds = edge.ComputeAABB(new Transform(position, 0f), 0);
                Assert.That(float.IsFinite(bounds.Left) && float.IsFinite(bounds.Right)
                    && float.IsFinite(bounds.Bottom) && float.IsFinite(bounds.Top), Is.True);
                Assert.That(MathF.Max(bounds.Width, bounds.Height), Is.GreaterThan(2 * edge.Radius));
                var expected = new EdgeShape(origin + delta, position).ComputeAABB(Transform.Empty, 0);
                Assert.That(Vector2.Distance(bounds.BottomLeft, expected.BottomLeft), Is.LessThan(.005f));
                Assert.That(Vector2.Distance(bounds.TopRight, expected.TopRight), Is.LessThan(.005f),
                    "Clamping the emitter offset must preserve the target endpoint.");
                var actualDelta = transform.GetWorldPosition(target) - transform.GetWorldPosition(source);
                if (actualDelta.Length() != 1)
                    Assert.That(Vector2.Distance(position, origin + Vector2.Normalize(actualDelta)), Is.LessThan(.005f),
                        "Non-degenerate beams must retain their existing emitter offset and collision span.");
            }

            IEnumerable<(EntityUid Uid, EdgeShape Edge)> BeamFixtures()
            {
                foreach (var fixture in em.EntityQuery<FixturesComponent>())
                    if (fixture.Fixtures.TryGetValue("BeamBody", out var body) && body.Shape is EdgeShape edge)
                        yield return (fixture.Owner, edge);
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task InvalidBeamInputsDoNotCreateBeamsOrReportSuccess()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var map = em.System<SharedMapSystem>().CreateMap(out _);
            var otherMap = em.System<SharedMapSystem>().CreateMap(out _);
            var source = em.SpawnEntity(null, new EntityCoordinates(map, Vector2.Zero));
            var samePosition = em.SpawnEntity(null, new EntityCoordinates(map, Vector2.Zero));
            var crossMap = em.SpawnEntity(null, new EntityCoordinates(otherMap, Vector2.UnitX));
            var listener = em.System<RejectedFixtureListener>();
            em.System<BeamSystem>().TryCreateBeam(source, samePosition, "HyperchargedLightning");
            em.System<BeamSystem>().TryCreateBeam(source, crossMap, "HyperchargedLightning");
            Assert.That(listener.Rejected, Is.Empty);
            Assert.That(em.EntityQuery<FixturesComponent>().Any(x => x.Fixtures.ContainsKey("BeamBody")), Is.False);
            Assert.That(em.EntityQuery<MetaDataComponent>().Any(x => x.EntityPrototype?.ID is "HyperchargedLightning" or "VirtualBeamEntityController"), Is.False);

            // A prototype without BeamComponent must not leak an entity or report a successful shot.
            var sourceBeam = em.AddComponent<BeamComponent>(source);
            var target = em.SpawnEntity(null, new EntityCoordinates(map, new Vector2(3, 0)));
            em.System<BeamSystem>().TryCreateBeam(source, target, "Pen");
            var rejected = em.EntityQuery<MetaDataComponent>().Single(x => x.EntityPrototype?.ID == "Pen");
            Assert.That(em.IsQueuedForDeletion(rejected.Owner), Is.True);
            Assert.That(sourceBeam.BeamShooter, Is.EqualTo(EntityUid.Invalid));
            Assert.That(listener.Rejected, Is.Empty);
        });
        await pair.CleanReturnAsync();
    }
}

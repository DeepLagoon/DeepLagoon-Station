#nullable enable annotations
using System;
using System.Collections;
using System.Numerics;
using System.Reflection;
using Content.Server.Physics.Controllers;
using Content.Server.Shuttles.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.Tag;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ShuttleInputScratchTests
{
    private delegate void InputHandler(EntityUid uid, ref GetShuttleInputsEvent args);

    private sealed class InputProbeSystem : EntitySystem
    {
        public InputHandler? Handler;
        public override void Initialize() => SubscribeLocalEvent<TagComponent, GetShuttleInputsEvent>(OnInput);
        private void OnInput(EntityUid uid, TagComponent component, ref GetShuttleInputsEvent args) => Handler?.Invoke(uid, ref args);
    }

    [Test]
    public async Task NativeInputAggregationPreservesSourcesAcrossShuttlesReentryAndExceptions()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var maps = em.System<SharedMapSystem>();
            var transform = em.System<SharedTransformSystem>();
            var mover = em.System<MoverController>();
            var probe = em.System<InputProbeSystem>();
            var first = map.Grid.Owner;
            var otherGrid = maps.CreateGridEntity(map.MapId);
            maps.SetTile(otherGrid.Owner, otherGrid.Comp, Vector2i.Zero, map.Tile.Tile);
            transform.SetWorldPosition(otherGrid.Owner, new Vector2(100, 0));
            var second = otherGrid.Owner;

            ShuttleComponent Setup(EntityUid uid)
            {
                var shuttle = em.EnsureComponent<ShuttleComponent>(uid);
                Array.Fill(shuttle.LinearThrust, 1000f);
                Array.Fill(shuttle.BaseLinearThrust, 1000f);
                Array.Fill(shuttle.RatedBaseLinearThrust, 1000f);
                Array.Fill(shuttle.AccelerationLinearThrust, 1000f);
                Array.Fill(shuttle.AccelerationRatedBaseLinearThrust, 1000f);
                shuttle.AngularThrust = 1000;
                return shuttle;
            }
            var firstShuttle = Setup(first);
            var secondShuttle = Setup(second);
            EntityUid Source()
            {
                var uid = em.SpawnEntity(null, map.GridCoords);
                em.AddComponent<TagComponent>(uid);
                return uid;
            }
            var left = Source();
            var right = Source();
            var shared = Source();
            mover.AddPilot(first, left);
            mover.AddPilot(first, right);
            mover.AddPilot(first, shared);
            mover.AddPilot(second, shared);

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var runMethod = typeof(MoverController).GetMethod("HandleShuttleMovement", flags)!;
            var inputField = typeof(MoverController).GetField("_shuttleInputBuffer", flags)!;
            var removeField = typeof(MoverController).GetField("_invalidShuttleSourceBuffer", flags)!;
            var busyField = typeof(MoverController).GetField("_shuttleInputBuffersInUse", flags)!;
            var inputBuffer = (IList) inputField.GetValue(mover)!;
            var removeBuffer = (IList) removeField.GetValue(mover)!;
            object Run(bool measure = false)
            {
                object?[] parameters = [1f / 60f, measure, null];
                runMethod.Invoke(mover, parameters);
                return parameters[2]!;
            }
            void AssertReleased()
            {
                Assert.That(inputField.GetValue(mover), Is.SameAs(inputBuffer));
                Assert.That(removeField.GetValue(mover), Is.SameAs(removeBuffer));
                Assert.That(inputBuffer.Count, Is.Zero);
                Assert.That(removeBuffer.Count, Is.Zero);
                Assert.That(busyField.GetValue(mover), Is.False);
            }

            var neutral = false;
            var reenter = false;
            var nested = false;
            var fail = false;
            var failure = new InvalidOperationException("fixture input failure");
            probe.Handler = (EntityUid uid, ref GetShuttleInputsEvent args) =>
            {
                if (uid == right)
                {
                    if (fail)
                        throw failure;
                    if (reenter && !nested)
                    {
                        nested = true;
                        Run();
                        nested = false;
                    }
                }
                if (uid == shared && (args.ShuttleUid == first || neutral))
                    return; // Invalid on the first ship, valid on the second in the same pass.
                args.GotInput = true;
                if (neutral && uid == right)
                    return; // Valid source without an active input must remain registered.
                args.Input = new ShuttleInput(uid == left ? -Vector2.UnitX : uid == right ? Vector2.UnitX : Vector2.UnitY, 0, 0);
                args.AccelMul = uid == right ? 3 : 1;
                args.AngularMul = uid == right ? 3 : 1;
                args.Autopilot = !neutral;
            };
            try
            {
                Run();
                Assert.That(em.GetComponent<PilotedShuttleComponent>(first).ActiveSources, Is.EqualTo(2));
                Assert.That(em.GetComponent<PilotedShuttleComponent>(first).InputSources, Does.Not.Contain(shared));
                Assert.That(em.GetComponent<PilotedShuttleComponent>(second).InputSources, Does.Contain(shared));
                Assert.That(firstShuttle.AccelerationMultiplier, Is.EqualTo(2));
                Assert.That(firstShuttle.LastThrust, Is.EqualTo(Vector2.Zero), "Equal opposite native inputs cancel.");
                Assert.That(secondShuttle.LastThrust.Y, Is.GreaterThan(0));
                Assert.That(em.HasComponent<ShuttleAutopilotActiveComponent>(first), Is.True);
                AssertReleased();

                // The outer pass has already collected its first input when the second source re-enters.
                reenter = true;
                var timing = Run(measure: true);
                reenter = false;
                Assert.That(firstShuttle.LastThrust, Is.EqualTo(Vector2.Zero));
                Assert.That(em.GetComponent<PilotedShuttleComponent>(first).ActiveSources, Is.EqualTo(2));
                Assert.That(timing.GetType().GetField("Sources")!.GetValue(timing), Is.EqualTo(3));
                Assert.That(timing.GetType().GetField("ActiveShuttles")!.GetValue(timing), Is.EqualTo(2));
                AssertReleased();

                fail = true;
                var error = Assert.Throws<TargetInvocationException>(() => Run());
                Assert.That(error!.InnerException, Is.SameAs(failure));
                AssertReleased();
                fail = false;

                neutral = true;
                Run();
                Assert.That(em.GetComponent<PilotedShuttleComponent>(first).InputSources, Does.Contain(right));
                Assert.That(em.GetComponent<PilotedShuttleComponent>(first).ActiveSources, Is.EqualTo(1));
                Assert.That(em.GetComponent<PilotedShuttleComponent>(second).ActiveSources, Is.Zero);
                Assert.That(secondShuttle.LastThrust, Is.EqualTo(Vector2.Zero));
                Assert.That(secondShuttle.AccelerationMultiplier, Is.EqualTo(1));
                Assert.That(em.HasComponent<ShuttleAutopilotActiveComponent>(first), Is.False);
                AssertReleased();
            }
            finally
            {
                probe.Handler = null;
            }
        });
        await pair.CleanReturnAsync();
    }
}

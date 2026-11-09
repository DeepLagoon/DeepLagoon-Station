#nullable enable
using System.Collections.Generic;
using Content.Server.Fluids.EntitySystems;
using Content.Shared.Chemistry.Components;
using Content.Shared.Mood;
using Content.Shared.Movement.Components;
using Content.Shared.Slippery;
using Content.Shared.Stunnable;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.Movement;

public sealed class SlippingTest : MovementTest
{
    public sealed class SlipTestSystem : EntitySystem
    {
        public HashSet<EntityUid> Slipped = new();
        public override void Initialize()
        {
            SubscribeLocalEvent<SlipperyComponent, SlipEvent>(OnSlip);
        }

        private void OnSlip(EntityUid uid, SlipperyComponent component, ref SlipEvent args)
        {
            Slipped.Add(args.Slipped);
        }
    }

    [Test]
    public async Task BananaSlipTest()
    {
        // Keep this movement fixture independent of mood bonuses.
        await Server.WaitPost(() =>
        {
            SEntMan.RemoveComponent<Content.Server.Mood.MoodComponent>(SPlayer);
            SEntMan.System<Content.Shared.Movement.Systems.MovementSpeedModifierSystem>()
                .RefreshMovementSpeedModifiers(SPlayer);
        });
        var sys = SEntMan.System<SlipTestSystem>();
        await SpawnTarget("TrashBananaPeel");

        var modifier = Comp<MovementSpeedModifierComponent>(Player).SprintSpeedModifier;
        Assert.That(modifier, Is.EqualTo(1), "Player is not moving at full speed.");

        // Player is to the left of the banana peel and has not slipped.
        Assert.That(Delta(), Is.GreaterThan(0.5f));
        Assert.That(sys.Slipped, Does.Not.Contain(SEntMan.GetEntity(Player)));

        // Walking over the banana slowly does not trigger a slip.
        await SetKey(EngineKeyFunctions.Walk, BoundKeyState.Down);
        await Move(DirectionFlag.East, 1f);
        Assert.That(Delta(), Is.LessThan(0.5f));
        Assert.That(sys.Slipped, Does.Not.Contain(SEntMan.GetEntity(Player)));
        AssertComp<KnockedDownComponent>(false, Player);

        // Moving at normal speeds does trigger a slip.
        await SetKey(EngineKeyFunctions.Walk, BoundKeyState.Up);
        await Move(DirectionFlag.West, 1f);
        Assert.That(sys.Slipped, Does.Contain(SEntMan.GetEntity(Player)));
        AssertComp<KnockedDownComponent>(true, Player);
    }

    [TestCase("TrashBananaPeel")]
    [TestCase("Soap")]
    [TestCase("ClownPDA")]
    [TestCase("Puddle")]
    public async Task SlowedSprintStillSlips(string prototype)
    {
        await Server.WaitAssertion(() =>
            SEntMan.EventBus.RaiseLocalEvent(SPlayer, new MoodEffectEvent("SurgeryPain")));

        if (prototype == "Puddle")
        {
            await Server.WaitAssertion(() =>
            {
                var puddles = SEntMan.System<PuddleSystem>();
                var coordinates = Transform.WithEntityId(SEntMan.GetCoordinates(TargetCoords), MapData.Grid.Owner);
                Assert.That(puddles.TrySpillAt(coordinates,
                    new Solution("Water", 20), out var puddle), Is.True);
                Target = SEntMan.GetNetEntity(puddle);
            });
            await RunTicks(5);
        }
        else
        {
            await SpawnTarget(prototype);
        }

        var speed = Comp<MovementSpeedModifierComponent>(Player);
        Assert.That(speed.CurrentSprintSpeed, Is.LessThan(3.5f), "The character must be slowed below the normal slip threshold.");
        Assert.That(speed.CurrentSprintSpeed, Is.GreaterThan(speed.CurrentWalkSpeed));

        await SetKey(EngineKeyFunctions.Walk, BoundKeyState.Down);
        await RunTicks(1);
        await Move(DirectionFlag.East, 1f);
        Assert.That(Delta(), Is.LessThan(0), "The character must cross the slippery entity while walking.");
        AssertComp<KnockedDownComponent>(false, Player);

        await SetKey(EngineKeyFunctions.Walk, BoundKeyState.Up);
        await RunTicks(1);
        await Move(DirectionFlag.West, 1f);
        AssertComp<KnockedDownComponent>(true, Player);
    }
}


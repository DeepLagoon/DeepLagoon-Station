using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Temperature.Components;
using Robust.Client.GameObjects;
using Robust.Shared;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;

namespace Content.IntegrationTests.Tests.Movement;

public sealed class NutritionMovementTest : MovementTest
{
    protected override bool AddWalls => false;

    [Test]
    public async Task SevereHungerAndThirstKeepPredictedMovementInSync()
    {
        await Client.WaitPost(() => Client.CfgMan.SetCVar(CVars.NetPredictTickBias, 6));
        await Server.WaitPost(() =>
        {
            // The test grid can cool the character and apply a separate movement penalty.
            SEntMan.RemoveComponent<TemperatureSpeedComponent>(SPlayer);
            SEntMan.System<MovementSpeedModifierSystem>().RefreshMovementSpeedModifiers(SPlayer);
            SetNeeds(25, 100);
        });
        await RunSeconds(1.25f);

        await Server.WaitPost(() => SetNeeds(0, 0));
        await RunSeconds(1.25f);

        float sprint = 0;
        await Server.WaitAssertion(() =>
        {
            sprint = SEntMan.GetComponent<MovementSpeedModifierComponent>(SPlayer).SprintSpeedModifier;
            Assert.That(sprint, Is.LessThan(0.5625f), "The test needs both the nutritional and mood slowdowns.");
            Assert.That(SEntMan.GetComponent<MovementSpeedModifierComponent>(SPlayer).WalkSpeedModifier,
                Is.EqualTo(0.5625f).Within(0.0001f));
        });
        await Client.WaitAssertion(() =>
        {
            Assert.That(CEntMan.GetComponent<HungerComponent>(CPlayer).CurrentThreshold, Is.EqualTo(HungerThreshold.Dead));
            Assert.That(CEntMan.GetComponent<ThirstComponent>(CPlayer).CurrentThirstThreshold, Is.EqualTo(ThirstThreshold.Dead));
            // A predicted equipment or status change can trigger a speed refresh between server updates.
            CEntMan.System<MovementSpeedModifierSystem>().RefreshMovementSpeedModifiers(CPlayer);
            AssertSpeed(CEntMan, CPlayer, 0.5625f, sprint);
        });

        float start = 0;
        await Client.WaitPost(() => start = GetRenderedX());
        var last = start;
        await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Down);
        for (var tick = 0; tick < 25; tick++)
        {
            await RunTicks(1);
            await Client.WaitAssertion(() =>
            {
                var current = GetRenderedX();
                Assert.That(current, Is.GreaterThanOrEqualTo(last - 0.001f),
                    $"Movement must not snap backwards at tick {tick}.");
                last = current;
            });
        }
        await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Up);
        Assert.That(last, Is.GreaterThan(start + 0.2f));

        await Server.WaitPost(() => SetNeeds(75, 450));
        await RunSeconds(1.25f);
        await AssertBothSpeeds(1f);

        await Server.WaitAssertion(() =>
        {
            SetNeeds(25, 450);
            Assert.That(SEntMan.GetComponent<MovementSpeedModifierComponent>(SPlayer).WalkSpeedModifier,
                Is.EqualTo(0.75f).Within(0.0001f));
        });
        await RunTicks(12);
        await AssertBothSpeeds(0.75f);
    }

    private float GetRenderedX()
    {
        var transforms = CEntMan.System<TransformSystem>();
        transforms.FrameUpdate(TickPeriod);
        return transforms.GetRenderWorldPosition(CPlayer).X;
    }

    private void SetNeeds(float hunger, float thirst)
    {
        SEntMan.System<HungerSystem>().SetHunger(SPlayer, hunger);
        SEntMan.System<ThirstSystem>().SetThirst(SPlayer, SEntMan.GetComponent<ThirstComponent>(SPlayer), thirst);
    }

    private async Task AssertBothSpeeds(float walk)
    {
        float sprint = 0;
        await Server.WaitAssertion(() =>
        {
            var movement = SEntMan.GetComponent<MovementSpeedModifierComponent>(SPlayer);
            sprint = movement.SprintSpeedModifier;
            Assert.That(movement.WalkSpeedModifier, Is.EqualTo(walk).Within(0.0001f));
        });
        await Client.WaitAssertion(() =>
        {
            CEntMan.System<MovementSpeedModifierSystem>().RefreshMovementSpeedModifiers(CPlayer);
            AssertSpeed(CEntMan, CPlayer, walk, sprint);
        });
    }

    private static void AssertSpeed(IEntityManager entities, EntityUid player, float walk, float sprint)
    {
        var movement = entities.GetComponent<MovementSpeedModifierComponent>(player);
        Assert.That(movement.WalkSpeedModifier, Is.EqualTo(walk).Within(0.0001f));
        Assert.That(movement.SprintSpeedModifier, Is.EqualTo(sprint).Within(0.0001f));
    }
}

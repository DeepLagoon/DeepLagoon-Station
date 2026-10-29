using System.Linq;
using Content.IntegrationTests.Tests.Interaction;
using Content.Shared._Goobstation.Tools.Components;
using Content.Shared.DoAfter;
using Content.Shared.Tools.Components;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.Weldable;

public sealed class AdminGhostWeldingSparksTests : InteractionTest
{
    protected override string PlayerPrototype => "AdminObserver";

    [TestCase("WallSolid")]
    [TestCase("ReinforcedWindow")]
    [TestCase("Airlock")]
    public async Task InstantWeldingDoesNotLeaveSparks(string prototype)
    {
        await SpawnTarget(prototype);
        await InteractUsing(Weld);

        await Server.WaitAssertion(() =>
        {
            Assert.That(DoAfters.NextId, Is.GreaterThan(0));
            var tool = Hands.ActiveHandEntity!.Value;
            Assert.That(SEntMan.GetComponent<WeldingSparksComponent>(tool).SpawnedEffects, Is.Empty);
            Assert.That(SEntMan.EntityQuery<MetaDataComponent>().Any(meta => meta.EntityPrototype?.ID == "EffectWeldingSparks"), Is.False);
        });
    }
}

public sealed class WeldingSparksTests : InteractionTest
{
    [Test]
    public async Task ZeroDurationWeldingDoesNotLeaveSparks()
    {
        await SpawnTarget(WeldableTests.Locker);
        await Server.WaitPost(() => SEntMan.GetComponent<WeldableComponent>(STarget!.Value).Time = TimeSpan.Zero);
        await InteractUsing(Weld);

        await Server.WaitAssertion(() =>
        {
            Assert.That(Comp<WeldableComponent>().IsWelded, Is.True);
            Assert.That(SEntMan.GetComponent<WeldingSparksComponent>(Hands.ActiveHandEntity!.Value).SpawnedEffects, Is.Empty);
            Assert.That(SEntMan.EntityQuery<MetaDataComponent>().Any(meta => meta.EntityPrototype?.ID == "EffectWeldingSparks"), Is.False);
        });
    }

    [TestCase("complete")]
    [TestCase("cancel")]
    [TestCase("delete tool")]
    [TestCase("remove sparks component")]
    [TestCase("remove do-after components")]
    [TestCase("delete user")]
    [TestCase("delete target")]
    public async Task SparksFollowActionLifetime(string ending)
    {
        await SpawnTarget(WeldableTests.Locker);
        await InteractUsing(Weld, awaitDoAfters: false);
        var tool = Hands.ActiveHandEntity!.Value;
        var sparks = SEntMan.GetComponent<WeldingSparksComponent>(tool);
        EntityUid effect = default;

        await Server.WaitAssertion(() =>
        {
            Assert.That(sparks.SpawnedEffects, Has.Count.EqualTo(1));
            effect = sparks.SpawnedEffects.Values.Single();
            Assert.That(SEntMan.EntityExists(effect), Is.True);
        });

        switch (ending)
        {
            case "complete":
                await AwaitDoAfters();
                Assert.That(Comp<WeldableComponent>().IsWelded, Is.True);
                break;
            case "cancel":
                await CancelDoAfters();
                Assert.That(Comp<WeldableComponent>().IsWelded, Is.False);
                break;
            case "delete tool":
                await Server.WaitPost(() => SEntMan.DeleteEntity(tool));
                break;
            case "remove sparks component":
                await Server.WaitPost(() => SEntMan.RemoveComponent<WeldingSparksComponent>(tool));
                break;
            case "remove do-after components":
                await Server.WaitPost(() => SEntMan.RemoveComponent<DoAfterComponent>(SPlayer));
                break;
            case "delete user":
                // Keep the tool alive so deleting its holder cannot hide an orphaned effect.
                await Server.WaitPost(() =>
                {
                    Assert.That(HandSys.TryDrop(SPlayer, handsComp: Hands), Is.True);
                    SEntMan.DeleteEntity(SPlayer);
                });
                break;
            case "delete target":
                await Server.WaitPost(() => SEntMan.DeleteEntity(STarget!.Value));
                break;
        }

        await RunTicks(5);
        await Server.WaitAssertion(() =>
        {
            Assert.That(sparks.SpawnedEffects, Is.Empty);
            Assert.That(SEntMan.EntityExists(effect), Is.False);
        });
    }
}

using Content.IntegrationTests.Tests.Interaction;
using Content.Server._DeepLagoon.Construction;
using Content.Server.Fluids.EntitySystems;
using Content.Shared._DeepLagoon.Construction;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using Robust.Client.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ShowerTests : InteractionTest
{
    [Test]
    public async Task ConstructionConsumesExactlyTenSteelAndClickSwitchesTheShower()
    {
        await StartConstruction("DLShower");
        await InteractUsing(Steel, 10);
        AssertPrototype("DLShower");
        Assert.That(Hands.ActiveHandEntity, Is.Null);
        await Interact();
        await Server.WaitAssertion(() =>
        {
            var shower = SEntMan.GetComponent<ShowerComponent>(STarget!.Value);
            Assert.That(shower.Running, Is.True);
            var appearance = SEntMan.System<SharedAppearanceSystem>();
            Assert.That(appearance.TryGetData<bool>(STarget.Value, ShowerVisuals.Water, out var water) && water, Is.True);
            Assert.That(appearance.TryGetData<bool>(STarget.Value, ShowerVisuals.Mist, out var mist) && mist, Is.False);
        });
        await Interact();
        await Server.WaitAssertion(() => Assert.That(SEntMan.GetComponent<ShowerComponent>(STarget!.Value).Running, Is.False));
    }

    [TestCase(0)]
    [TestCase(90)]
    [TestCase(180)]
    [TestCase(270)]
    public async Task MistRequiresContinuousWorkAndPuddleStaysSmall(int degrees)
    {
        await SpawnTarget("DLShower");
        await Server.WaitPost(() => Transform.SetLocalRotation(STarget!.Value, Angle.FromDegrees(degrees)));
        await Interact();
        await RunSeconds(4f);
        await Server.WaitAssertion(() => Assert.That(SEntMan.GetComponent<ShowerComponent>(STarget!.Value).Mist, Is.False));
        await RunSeconds(2f);
        await Client.WaitAssertion(() =>
        {
            var sprite = CEntMan.GetComponent<SpriteComponent>(CTarget!.Value);
            Assert.That(sprite[sprite.LayerMapGet(ShowerVisualLayers.Water)].Visible, Is.True);
            Assert.That(sprite[sprite.LayerMapGet(ShowerVisualLayers.Mist)].Visible, Is.True);
        });
        await Server.WaitAssertion(() =>
        {
            var shower = SEntMan.GetComponent<ShowerComponent>(STarget!.Value);
            Assert.That(shower.Mist, Is.True);
            var xform = SEntMan.GetComponent<TransformComponent>(STarget.Value);
            Assert.That(xform.LocalRotation.Degrees, Is.EqualTo(Angle.FromDegrees(degrees).Degrees).Within(.01));
            var tile = MapMan.GetTileRef(MapData.Grid, MapData.Grid.Comp, xform.Coordinates);
            var puddles = SEntMan.System<PuddleSystem>();
            Assert.That(puddles.TryGetPuddle(tile, out var puddle), Is.True);
            Assert.That(puddles.CurrentVolume(puddle).Float(), Is.InRange(0.001f, 10f));
            // Simulate many scheduled emissions without waiting minutes or building timer debt.
            for (var i = 0; i < 50; i++)
            {
                shower.NextSpill = Server.ResolveDependency<IGameTiming>().CurTime;
                SEntMan.System<ShowerSystem>().Update(0f);
            }
            Assert.That(puddles.CurrentVolume(puddle).Float(), Is.LessThanOrEqualTo(10f));
        });
        await Interact();
        await Client.WaitAssertion(() =>
        {
            var sprite = CEntMan.GetComponent<SpriteComponent>(CTarget!.Value);
            Assert.That(sprite[sprite.LayerMapGet(ShowerVisualLayers.Water)].Visible, Is.False);
            Assert.That(sprite[sprite.LayerMapGet(ShowerVisualLayers.Mist)].Visible, Is.False);
        });
        await Server.WaitAssertion(() =>
        {
            var appearance = SEntMan.System<SharedAppearanceSystem>();
            Assert.That(appearance.TryGetData<bool>(STarget!.Value, ShowerVisuals.Water, out var water) && water, Is.False);
            Assert.That(appearance.TryGetData<bool>(STarget.Value, ShowerVisuals.Mist, out var mist) && mist, Is.False);
        });
        await Interact();
        await RunSeconds(4f);
        await Server.WaitAssertion(() => Assert.That(SEntMan.GetComponent<ShowerComponent>(STarget!.Value).Mist, Is.False,
            "Restarting the shower must reset the five-second mist delay"));
        await Delete(STarget!.Value);
        await RunSeconds(3f);
    }
}

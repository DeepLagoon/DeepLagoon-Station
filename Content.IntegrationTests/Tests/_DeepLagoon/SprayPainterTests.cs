using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server.Atmos.Piping.Components;
using Content.Server.Decals;
using Content.Shared.Decals;
using Content.Shared.Doors.Components;
using Content.Shared.SprayPainter;
using Content.Shared.SprayPainter.Components;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class SprayPainterTests : InteractionTest
{
    private EntityUid Painter => HandSys.GetActiveItem(SPlayer)!.Value;

    private void Message(BoundUserInterfaceMessage message, EntityUid? painter = null)
    {
        message.Actor = SPlayer;
        message.UiKey = SprayPainterUiKey.Key;
        SEntMan.EventBus.RaiseLocalEvent(painter ?? Painter, (object) message);
    }

    [Test]
    public async Task ModesCycleInHandAndKeepSeparateColorsAndPresets()
    {
        await PlaceInHands("SprayPainter");
        EntityUid other = default;
        await Server.WaitAssertion(() =>
        {
            other = SEntMan.SpawnEntity("SprayPainter", MapData.GridCoords);
            Message(new SprayPainterCustomColorMessage(Color.Purple));
        });
        await UseInHand();
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<SprayPainterComponent>(Painter).Mode, Is.EqualTo(SprayPainterMode.Pipes));
            Message(new SprayPainterColorPickedMessage("distro"));
            var component = SEntMan.GetComponent<SprayPainterComponent>(Painter);
            Assert.That(component.PipeColor, Is.EqualTo(component.ColorPalette["distro"]));
            Message(new SprayPainterCustomColorMessage(Color.Cyan));
            Assert.That(component.PickedColor, Is.Null);
        });
        await UseInHand();
        await Server.WaitPost(() => Message(new SprayPainterCustomColorMessage(Color.Green)));
        await UseInHand();
        await Server.WaitAssertion(() =>
        {
            var component = SEntMan.GetComponent<SprayPainterComponent>(Painter);
            Assert.That(component.Mode, Is.EqualTo(SprayPainterMode.Airlocks));
            Assert.That(component.AirlockColor, Is.EqualTo(Color.Purple));
            Assert.That(component.PipeColor, Is.EqualTo(Color.Cyan));
            Assert.That(component.DecalColor, Is.EqualTo(Color.Green));
            Assert.That(SEntMan.GetComponent<SprayPainterComponent>(other).Mode, Is.EqualTo(SprayPainterMode.Airlocks));
            Assert.That(SEntMan.GetComponent<SprayPainterComponent>(other).AirlockColor, Is.EqualTo(Color.White));
        });
    }

    [Test]
    public async Task ExistingAirlockStylesAndPipesAcceptCustomColors()
    {
        await PlaceInHands("SprayPainter");
        await SpawnTarget("Airlock");
        await RunTicks(10);
        await Client.WaitAssertion(() => Assert.That(CEntMan.GetComponent<PaintableAirlockComponent>(CTarget!.Value).PaintColor,
            Is.EqualTo(Color.White)));
        await Server.WaitPost(() => Message(new SprayPainterCustomColorMessage(Color.Purple)));
        await Interact();
        await Server.WaitAssertion(() => Assert.That(SEntMan.GetComponent<PaintableAirlockComponent>(STarget!.Value).PaintColor,
            Is.EqualTo(Color.Purple)));
        await RunTicks(10);
        await Client.WaitAssertion(() =>
        {
            var target = CTarget!.Value;
            Assert.That(CEntMan.GetComponent<PaintableAirlockComponent>(target).PaintColor, Is.EqualTo(Color.Purple),
                "The changed paint state must arrive on the actual connected client");
            var sprite = CEntMan.GetComponent<SpriteComponent>(target);
            Assert.That(CEntMan.System<SpriteSystem>().LayerMapTryGet((target, sprite), DoorVisualLayers.Base, out var layer, false), Is.True);
            Assert.That(sprite[layer].Color, Is.EqualTo(Color.Purple),
                "The generated state callback must update the existing airlock sprite after replication");
        });
        await Delete(STarget!.Value);
        await SpawnTarget("GasPipeStraight");
        await UseInHand();
        await Server.WaitPost(() => Message(new SprayPainterCustomColorMessage(Color.Cyan)));
        await Interact();
        await Server.WaitAssertion(() => Assert.That(SEntMan.GetComponent<AtmosPipeColorComponent>(STarget!.Value).Color,
            Is.EqualTo(Color.Cyan)));
    }

    [TestCase(true, 0)]
    [TestCase(false, 0)]
    [TestCase(true, 90)]
    [TestCase(false, 90)]
    public async Task DrawingUsesSavedDecalSettingsAndAlwaysCreatesCleanableDecals(bool mapRelativeClick, int gridRotation)
    {
        await Server.WaitPost(() =>
        {
            var target = Transform.WithEntityId(SEntMan.GetCoordinates(TargetCoords), MapData.Grid).Offset(new Vector2(.1f, .1f));
            var player = Transform.WithEntityId(SEntMan.GetCoordinates(PlayerCoords), MapData.Grid);
            Transform.SetCoordinates(SPlayer, player);
            Transform.SetLocalRotation(MapData.Grid, Angle.FromDegrees(gridRotation));
            TargetCoords = SEntMan.GetNetCoordinates(mapRelativeClick
                ? Transform.WithEntityId(target, MapData.MapUid) : target);
        });
        await PlaceInHands("SprayPainter");
        await UseInHand();
        await UseInHand();
        await Server.WaitAssertion(() =>
        {
            Message(new SprayPainterDecalSettingsMessage(
                new Decal(Vector2.Zero, "heart", Color.Purple, Angle.FromDegrees(45), 3, false), false));
            var selected = SEntMan.GetComponent<SprayPainterComponent>(Painter);
            Assert.That(selected.Mode, Is.EqualTo(SprayPainterMode.Decals));
            Assert.That(selected.DecalId, Is.EqualTo("heart"));
        });
        await Interact();
        await Server.WaitAssertion(() =>
        {
            var position = Transform.WithEntityId(SEntMan.GetCoordinates(TargetCoords), MapData.Grid);
            var drawn = SEntMan.System<DecalSystem>().GetDecalsInRange(MapData.Grid, position.Position)
                .Where(entry => entry.Decal.Id == "heart").ToArray();
            Assert.That(drawn.Length, Is.EqualTo(1), "The real floor click must place exactly one selected decal");
            var decal = drawn.Single().Decal;
            Assert.That(decal.Color, Is.EqualTo(Color.Purple));
            Assert.That(decal.Angle.Degrees, Is.EqualTo(45).Within(0.01));
            Assert.That(decal.ZIndex, Is.EqualTo(3));
            Assert.That(decal.Cleanable, Is.True, "The client cannot create permanent decals with a spray painter");
            Assert.That((decal.Coordinates - position.Position + new Vector2(.5f, .5f)).Length(), Is.LessThan(.001f),
                "The decal offset must use the rotated grid's local axes");
        });
    }

    [Test]
    public async Task ReachableMapRelativeSpaceClickCannotCreateADecal()
    {
        await PlaceInHands("SprayPainter");
        await UseInHand();
        await UseInHand();
        var initial = 0;
        await Server.WaitAssertion(() =>
        {
            initial = SEntMan.GetComponent<DecalGridComponent>(MapData.Grid).DecalIndex.Count;
            var space = new EntityCoordinates(MapData.Grid, 1.5f, -.5f);
            Assert.That(MapSystem.TryFindGridAt(Transform.ToMapCoordinates(space), out _, out _), Is.False);
            TargetCoords = SEntMan.GetNetCoordinates(Transform.WithEntityId(space, MapData.MapUid));
            Assert.That(InteractSys.InRangeUnobstructed(SPlayer, SEntMan.GetCoordinates(TargetCoords)), Is.True);
        });
        await Interact();
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.GetComponent<DecalGridComponent>(MapData.Grid).DecalIndex.Count, Is.EqualTo(initial)));
    }

    [Test]
    public async Task PreviewPlacementRequiresHeldToolReachAndValidSelection()
    {
        await PlaceInHands("SprayPainter");
        await UseInHand();
        await UseInHand();
        await Server.WaitAssertion(() =>
        {
            var decals = SEntMan.System<DecalSystem>();
            var initial = SEntMan.GetComponent<DecalGridComponent>(MapData.Grid).DecalIndex.Count;
            var at = Transform.WithEntityId(SEntMan.GetCoordinates(TargetCoords), MapData.Grid).Offset(new Vector2(-.5f, -.5f));
            var decal = new Decal(at.Position, "heart", Color.Blue, Angle.Zero, 0, false);
            Message(new SprayPainterPlaceDecalMessage(SEntMan.GetNetCoordinates(at), decal));
            Assert.That(SEntMan.GetComponent<DecalGridComponent>(MapData.Grid).DecalIndex.Count, Is.EqualTo(initial + 1));
            var drawn = decals.GetDecalsInRange(MapData.Grid, at.Position).Single(x => x.Decal.Id == "heart").Decal;
            Assert.That(drawn.Cleanable, Is.True);

            Message(new SprayPainterPlaceDecalMessage(SEntMan.GetNetCoordinates(new EntityCoordinates(MapData.Grid, 50, 50)), decal));
            Message(new SprayPainterPlaceDecalMessage(SEntMan.GetNetCoordinates(at), decal.WithId("does-not-exist")));
            Message(new SprayPainterPlaceDecalMessage(SEntMan.GetNetCoordinates(at), decal.WithZIndex(int.MaxValue)));
            Message(new SprayPainterPlaceDecalMessage(SEntMan.GetNetCoordinates(at), decal.WithColor(new Color(float.NaN, 0, 0))));
            var unheld = SEntMan.SpawnEntity("SprayPainter", MapData.GridCoords);
            SEntMan.GetComponent<SprayPainterComponent>(unheld).Mode = SprayPainterMode.Decals;
            Message(new SprayPainterPlaceDecalMessage(SEntMan.GetNetCoordinates(at), decal), unheld);
            Assert.That(SEntMan.GetComponent<DecalGridComponent>(MapData.Grid).DecalIndex.Count, Is.EqualTo(initial + 1));

            // Tool erasure cannot remove a permanent map decal beside the washable one.
            Assert.That(decals.TryAddDecal("heart", at, out var permanent, cleanable: false), Is.True);
            Message(new SprayPainterEraseDecalMessage(SEntMan.GetNetCoordinates(at)));
            var remaining = decals.GetDecalsInRange(MapData.Grid, at.Position);
            Assert.That(remaining.Any(x => x.Index == permanent), Is.True);
            Assert.That(remaining.Count(x => x.Decal.Id == "heart"), Is.EqualTo(1));
        });
    }
}

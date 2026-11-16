using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Client.Tabletop.UI;
using Content.Client.Viewport;
using Content.Shared.Input;
using Content.Shared.Tabletop.Components;
using Content.Shared.Tabletop.Events;
using Moq;
using Robust.Client.ComponentTrees;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using ClientTabletop = Content.Client.Tabletop.TabletopSystem;
using ServerTabletop = Content.Server.Tabletop.TabletopSystem;
using ViewSubscriberSystem = Robust.Server.GameObjects.ViewSubscriberSystem;

namespace Content.IntegrationTests.Tests.Interaction;

public sealed class TabletopChessTest : InteractionTest
{
    [Test]
    public async Task OpensWhenCameraArrivesAfterPlayEvent()
    {
        var table = await SpawnChessBoard();
        var receivedPlayEvent = false;
        var camera = EntityUid.Invalid;
        NetEntity netCamera = default;
        var net = CEntMan.EntityNetManager;

        void OnMessage(object sender, object message)
        {
            if (message is TabletopPlayEvent play && play.TableUid == table)
                receivedPlayEvent = true;
        }

        await Client.WaitPost(() => net.ReceivedSystemMessage += OnMessage);
        try
        {
            await Server.WaitPost(() =>
            {
                var existingViews = ServerSession.ViewSubscriptions.ToHashSet();
                SEntMan.System<ServerTabletop>().OpenSessionFor(ServerSession, ToServer(table));
                camera = ServerSession.ViewSubscriptions.Single(view => !existingViews.Contains(view));
                netCamera = SEntMan.GetNetEntity(camera);
                // Hold the camera out of PVS while the real server sends the play event.
                SEntMan.System<ViewSubscriberSystem>().RemoveViewSubscriber(camera, ServerSession);
            });
            await Pair.ReallyBeIdle(12);
            await Client.WaitAssertion(() =>
            {
                Assert.That(receivedPlayEvent, Is.True);
                Assert.That(CEntMan.TryGetEntity(netCamera, out _), Is.False);
                Assert.That(UiMan.WindowRoot.Children.OfType<TabletopWindow>(), Is.Empty);
            });

            await Server.WaitPost(() => SEntMan.System<ViewSubscriberSystem>().AddViewSubscriber(camera, ServerSession));
            await Pair.ReallyBeIdle(12);
            await Client.WaitAssertion(() =>
            {
                var window = UiMan.WindowRoot.Children.OfType<TabletopWindow>().Single();
                var eye = window.FindControl<ScalingViewport>("ScalingVp").Eye!;
                Assert.That(eye.Position.MapId, Is.EqualTo(CEntMan.GetComponent<TransformComponent>(ToClient(netCamera)).MapID));
                Assert.That(eye.Position.MapId, Is.Not.EqualTo(Robust.Shared.Map.MapId.Nullspace));
                window.Close();
            });
            await Pair.ReallyBeIdle(5);
            await Server.WaitAssertion(() => Assert.That(SEntMan.EntityExists(camera), Is.False));
        }
        finally
        {
            await Client.WaitPost(() => net.ReceivedSystemMessage -= OnMessage);
        }
    }

    [Test]
    public async Task RendersChessCameraAndDragsPawn()
    {
        var table = await SpawnChessBoard();
        await PressKey(ContentKeyFunctions.ActivateItemInWorld, cursorEntity: table);
        await Pair.ReallyBeIdle(12);

        var input = new Mock<IInputManager>();
        var inputField = typeof(ClientTabletop).GetField("_inputManager", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var tabletop = CEntMan.System<ClientTabletop>();
        var originalInput = inputField.GetValue(tabletop);
        TabletopWindow window = null;
        ScalingViewport viewport = null;
        ScreenCoordinates mouse = default;
        NetEntity pawn = default;
        MapCoordinates destination = default;
        input.SetupGet(manager => manager.MouseScreenPosition).Returns(() => mouse);

        try
        {
            await Client.WaitAssertion(() =>
            {
                window = UiMan.WindowRoot.Children.OfType<TabletopWindow>().Single();
                viewport = window.FindControl<ScalingViewport>("ScalingVp");
                var eye = viewport.Eye!;
                Assert.That(eye.Position.MapId, Is.Not.EqualTo(MapId).And.Not.EqualTo(Robust.Shared.Map.MapId.Nullspace));
                Assert.That(viewport, Is.Not.SameAs(Client.ResolveDependency<IEyeManager>().MainViewport));
                AssertChessCameraRendersBeforeUi(viewport);

                var viewBounds = Box2.CenteredAround(eye.Position.Position, (Vector2) viewport.ViewportSize / EyeManager.PixelsPerMeter);
                var sprites = CEntMan.System<SpriteTreeSystem>().QueryAabb(eye.Position.MapId, viewBounds);
                Assert.That(sprites.Count(sprite => CEntMan.GetComponent<MetaDataComponent>(sprite.Uid).EntityPrototype?.ID == "ChessBoardTabletop"), Is.EqualTo(1));
                Assert.That(sprites.Count(sprite => CEntMan.HasComponent<TabletopDraggableComponent>(sprite.Uid)), Is.EqualTo(34));

                var pawnUid = sprites.First(sprite => CEntMan.GetComponent<MetaDataComponent>(sprite.Uid).EntityPrototype?.ID == "WhitePawn").Uid;
                pawn = CEntMan.GetNetEntity(pawnUid);
                var start = CEntMan.System<SharedTransformSystem>().GetMapCoordinates(pawnUid);
                destination = start.Offset(Vector2.UnitY);
                mouse = new ScreenCoordinates(viewport.WorldToScreen(start.Position), viewport.Window!.Id);
                Assert.That(UiMan.MouseGetControl(mouse), Is.SameAs(viewport));

                // Headless Clyde has no physical mouse. Supply its position while keeping gameplay input routing.
                inputField.SetValue(tabletop, input.Object);
                InputManager.ViewportKeyEvent(viewport, new BoundKeyEventArgs(EngineKeyFunctions.Use, BoundKeyState.Down, mouse, true));
            });
            await Pair.ReallyBeIdle(5);
            await Server.WaitAssertion(() => Assert.That(
                SEntMan.GetComponent<TabletopDraggableComponent>(ToServer(pawn)).DraggingPlayer,
                Is.EqualTo(ServerSession.UserId)));

            await Client.WaitPost(() => mouse = new ScreenCoordinates(viewport.WorldToScreen(destination.Position), mouse.Window));
            await Pair.ReallyBeIdle(10);
            await Client.WaitPost(() => InputManager.ViewportKeyEvent(viewport,
                new BoundKeyEventArgs(EngineKeyFunctions.Use, BoundKeyState.Up, mouse, true)));
            await Pair.ReallyBeIdle(5);

            await Server.WaitAssertion(() =>
            {
                var piece = ToServer(pawn);
                var actual = Transform.GetMapCoordinates(piece);
                Assert.That(actual.MapId, Is.EqualTo(destination.MapId));
                Assert.That(Vector2.Distance(actual.Position, destination.Position), Is.LessThan(0.01f));
                Assert.That(SEntMan.GetComponent<TabletopDraggableComponent>(piece).DraggingPlayer, Is.Null);
            });
        }
        finally
        {
            await Client.WaitPost(() =>
            {
                inputField.SetValue(tabletop, originalInput);
                window?.Close();
            });
        }
    }

    private async Task<NetEntity> SpawnChessBoard()
    {
        NetEntity table = default;
        await Server.WaitPost(() =>
        {
            Server.CfgMan.SetCVar(Robust.Shared.CVars.NetPVS, true);
            table = SEntMan.GetNetEntity(SEntMan.SpawnEntity("ChessBoard", SEntMan.GetCoordinates(TargetCoords)));
        });
        await Pair.ReallyBeIdle(12);
        return table;
    }

    private static void AssertChessCameraRendersBeforeUi(ScalingViewport viewport)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var field = typeof(ScalingViewport).GetField("_viewport", flags)!;
        var original = (IClydeViewport) field.GetValue(viewport)!;
        Assert.That(original.AutomaticRender, Is.True);
        Assert.That(original.Eye, Is.SameAs(viewport.Eye));

        var draw = typeof(ScalingViewport).GetMethod("Draw", flags | BindingFlags.DeclaredOnly)!;
        var clydeViewport = new Mock<IClydeViewport>();
        var texture = new Mock<Texture>(Vector2i.One).Object;
        var target = new Mock<IRenderTexture>();
        target.SetupGet(t => t.Texture).Returns(texture);
        clydeViewport.SetupGet(v => v.AutomaticRender).Returns(true);
        clydeViewport.SetupGet(v => v.Size).Returns(viewport.ViewportSize);
        clydeViewport.SetupGet(v => v.RenderTarget).Returns(target.Object);
        clydeViewport.SetupProperty(v => v.Eye, viewport.Eye);
        var handle = new Mock<IRenderHandle>();
        handle.SetupGet(h => h.DrawingHandleScreen).Returns(new Mock<DrawingHandleScreen>(texture).Object);

        field.SetValue(viewport, clydeViewport.Object);
        try
        {
            draw.Invoke(viewport, [handle.Object]);
            clydeViewport.Verify(v => v.Render(), Times.Never);
        }
        finally
        {
            field.SetValue(viewport, original);
        }
    }
}

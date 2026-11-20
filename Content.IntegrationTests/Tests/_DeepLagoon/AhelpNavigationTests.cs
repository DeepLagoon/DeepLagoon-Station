using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Client._DeepLagoon.Ahelp;
using Content.Client.Administration.Systems;
using Content.Server._DeepLagoon.Ahelp;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server.Shuttles.Components;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._DeepLagoon.Ahelp;
using Robust.Client.UserInterface.Controls;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class AhelpNavigationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task HistoryReturnsToCurrentConversationAndIgnoresLateReplies(bool admin)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Client.WaitAssertion(() =>
        {
            var type = typeof(AhelpWindow);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var actor = Guid.NewGuid();
            var target = admin ? Guid.NewGuid() : actor;
            var current = new AhelpTicket(Guid.NewGuid(), 10, target, "Player", null, null, AhelpStatus.New, 1);
            var archived = current with { Id = Guid.NewGuid(), Number = 9, Status = AhelpStatus.Closed };
            // Keep the window outside the UI tree and inject snapshots directly; no network ticks run.
            using var window = new AhelpWindow(new BwoinkSystem(), actor, admin);
            object Field(string name) => type.GetField(name, flags)!.GetValue(window);
            void Set(string name, object value) => type.GetField(name, flags)!.SetValue(window, value);
            void Call(string name, params object[] args) => type.GetMethod(name, flags)!.Invoke(window, args);
            bool Writable() => (bool) type.GetProperty("CanWrite", flags)!.GetValue(window);
            void Reply(AhelpTicket[] tickets, AhelpTicket selected, AhelpLine[] lines)
            {
                var operation = Guid.NewGuid();
                Set("_readId", operation);
                Call("OnState", new AhelpSnapshot(true, tickets, selected, lines, 0, operation: operation, round: "test-round"));
            }

            window.FocusPlayer(target);
            Reply(new[] { current }, current, Array.Empty<AhelpLine>());
            var draft = (LineEdit) Field("_draft");
            draft.Text = "unsent draft";
            Assert.That(Writable(), Is.True);
            var key = type.GetProperty("ConversationKey", flags)!.GetValue(window);
            Call("OnWebAction", "draft", $"{{\"text\":\"web draft\",\"round\":\"test-round\",\"conversation\":\"{key}\"}}");
            Assert.That(draft.Text, Is.EqualTo("web draft"));
            Call("OpenHistory");
            Reply(new[] { archived }, null, Array.Empty<AhelpLine>());
            Assert.That(((ScrollContainer) Field("_historyList")).Visible, Is.True);
            Assert.That(Writable(), Is.False);
            var native = (BoxContainer) Field("_native");
            Assert.That(native.Visible, Is.False);
            var historyLine = new AhelpLine(1, target, "Player", "old message", false, 1);
            Reply(new[] { archived }, archived, new[] { historyLine });
            Set("_nativeFallback", true);
            Call("Render");
            Assert.That(native.Visible, Is.True);
            Assert.That(draft.Parent!.Visible, Is.False);
            Assert.That(((BoxContainer) Field("_toolbar")).Children.OfType<Button>().Select(b => b.Text),
                Is.EqualTo(new[] { "← К списку истории", admin ? "← К текущим тикетам" : "← К обращению" }));

            var late = Guid.NewGuid();
            Set("_operation", late);
            Call("OnState", new AhelpSnapshot(true, new[] { current }, current, Array.Empty<AhelpLine>(), 0,
                operation: late, round: "test-round"));
            Assert.That(Field("_ticket"), Is.EqualTo(archived.Id));
            Assert.That(Writable(), Is.False);
            Call("ShowHistoryList");
            Assert.That(((ScrollContainer) Field("_historyList")).Visible, Is.True);
            Call("ReturnFromHistory");
            Assert.That(Field("_ticket"), Is.EqualTo(current.Id));
            Assert.That(Field("_target"), Is.EqualTo(target));
            Reply(new[] { current }, current, Array.Empty<AhelpLine>());
            Assert.That(Writable(), Is.True);
            Assert.That(draft.Text, Is.EqualTo("web draft"));
            Assert.That(draft.Parent!.Visible, Is.True);
            Call("OpenHistory");
            Call("OnState", new AhelpSnapshot(true, Array.Empty<AhelpTicket>(), null, Array.Empty<AhelpLine>(), 0, round: "next-round"));
            Assert.That(Field("_historyMode"), Is.False);
            Assert.That(Field("_ticket"), Is.EqualTo(Guid.Empty));
            Assert.That(draft.Text, Is.Empty);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BodyGhostAndShipAreResolvedSeparatelyWithoutClientVisibility()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var nav = entities.System<AhelpTeleportSystem>();
            var minds = entities.System<SharedMindSystem>();
            var player = new NetUserId(Guid.NewGuid());
            var mind = minds.CreateMind(player);
            var body = entities.SpawnEntity(null, map.GridCoords);
            var ghost = entities.SpawnEntity("MobObserver", new EntityCoordinates(map.Grid, new Vector2(1000, 1000)));
            minds.TransferTo(mind, body);
            minds.Visit(mind, ghost);
            entities.EnsureComponent<ShuttleComponent>(map.Grid);
            entities.EnsureComponent<ShipOwnershipComponent>(map.Grid).OwnerUserId = player;
            Assert.That(nav.Resolve(player, "body").Entity, Is.EqualTo(body));
            Assert.That(nav.Resolve(player, "ghost").Entity, Is.EqualTo(ghost));
            Assert.That(nav.Resolve(player, "ship").Entity, Is.EqualTo(map.Grid.Owner));
            Assert.That(nav.Inspect(player.UserId).BodyError, Is.Empty);
            Assert.That(nav.Inspect(player.UserId).GhostError, Is.Empty);
            minds.UnVisit(mind);
            entities.DeleteEntity(ghost);
            Assert.That(nav.Resolve(player, "ghost").Error, Is.EqualTo("У игрока нет призрака."));
            var parking = entities.EnsureComponent<ParkingShuttleComponent>(map.Grid);
            parking.OwnerAccount = player.UserId;
            parking.Parked = true;
            Assert.That(nav.Resolve(player, "ship").Error, Is.EqualTo("Корабль игрока находится на парковке."));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LobbyHasNoTeleportButtonsAndReportsTheActualReason()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Server.WaitAssertion(() =>
        {
            var player = pair.Server.ResolveDependency<IPlayerManager>().Sessions.Single();
            var nav = pair.Server.EntMan.System<AhelpTeleportSystem>();
            var state = nav.Inspect(player.UserId.UserId);
            Assert.That(new[] { state.ShipError, state.BodyError, state.GhostError }, Is.All.EqualTo("Игрок в лобби."));
            Assert.That(nav.Inspect(Guid.Empty).BodyError, Is.EqualTo("Выберите игрока."));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NativeHistoryWrapsMessagesAndLongFileNamesWhenResized()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Client.WaitAssertion(() =>
        {
            var wrappedText = typeof(AhelpWindow).GetMethod("WrappedText", BindingFlags.Static | BindingFlags.NonPublic)!;
            var text = (RichTextLabel) wrappedText.Invoke(null, new object[] { string.Concat(Enumerable.Repeat("Длинное сообщение с переносом. ", 25)), null });
            var filename = (RichTextLabel) wrappedText.Invoke(null, new object[] { new string('a', 200) + ".png", null });
            var history = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true,
                Children = { text, filename } };
            var scroll = (ScrollContainer) Activator.CreateInstance(typeof(AhelpWindow).GetNestedType("TicketHistoryScroll", BindingFlags.NonPublic)!, true)!;
            scroll.AddChild(history);
            scroll.Measure(new Vector2(420, 180));
            scroll.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(420, 180)));
            var wideHeight = text.Height;
            Assert.That(scroll.HScrollEnabled, Is.False);
            Assert.That(filename.Width, Is.LessThanOrEqualTo(scroll.Width));
            scroll.Measure(new Vector2(220, 180));
            scroll.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(220, 180)));
            Assert.That(text.Height, Is.GreaterThan(wideHeight));
            Assert.That(text.Width, Is.LessThanOrEqualTo(scroll.Width));
            Assert.That(filename.Width, Is.LessThanOrEqualTo(scroll.Width));
            Assert.That(history.Width, Is.LessThanOrEqualTo(scroll.Width));
        });
        await pair.CleanReturnAsync();
    }
}

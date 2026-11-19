using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Client._DeepLagoon.Ahelp;
using Content.Server._DeepLagoon.Ahelp;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server.Shuttles.Components;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared._NF.Shipyard.Components;
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

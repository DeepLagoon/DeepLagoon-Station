using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Client._DeepLagoon.Economy;
using Content.Server._DeepLagoon.Economy;
using Content.Server.CartridgeLoader;
using Content.Shared._DeepLagoon.Economy;
using Content.Shared.PDA;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class CharacterNotificationsTests
{
    [Test]
    public async System.Threading.Tasks.Task NotificationHistoryRequiresTheHoldersOpenActiveProgram()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            var actor = e.SpawnEntity("MobHuman", map.GridCoords); pair.Server.PlayerMan.SetAttachedEntity(session, actor);
            var pda = e.SpawnEntity("PassengerPDA", new EntityCoordinates(actor, Vector2.Zero));
            var loader = e.System<CartridgeLoaderSystem>();
            Assert.That(loader.TryGetProgram<CharacterNotificationsCartridgeComponent>(pda, out var program, installedOnly: true), Is.True);
            var notices = e.System<CharacterNotificationsSystem>();
            Assert.That(notices.CanRead(session, pda), Is.False);
            e.System<SharedUserInterfaceSystem>().OpenUi(pda, PdaUiKey.Key, actor);
            loader.ActivateProgram(pda, program!.Value);
            Assert.That(notices.CanRead(session, pda), Is.True);
            var other = e.SpawnEntity("MobHuman", map.GridCoords);
            e.System<SharedTransformSystem>().SetCoordinates(pda, new EntityCoordinates(other, Vector2.Zero));
            Assert.That(notices.CanRead(session, pda), Is.False, "Another holder's PDA cannot expose private character history.");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async System.Threading.Tasks.Task UnchangedNotificationHistoryKeepsItsExistingControls()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Client.WaitAssertion(() =>
        {
            using var view = new CharacterNotificationsView();
            pair.Client.ResolveDependency<IUserInterfaceManager>().StateRoot.AddChild(view);
            var rows = (BoxContainer)typeof(CharacterNotificationsView).GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            var notice = new CharacterNoticeView("sale-1", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "Ваш товар купили", "Сталь ×30. Зачислено 3000 спессо.", 3000, false);
            view.Apply([notice]); var first = rows.Children.First();
            view.Apply([notice with { Read = true }]);
            Assert.That(rows.Children.First(), Is.SameAs(first), "Polling and read receipts must not rebuild a visible history or reset scrolling.");
            view.Measure(new Vector2(520, 400)); view.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(520, 400)));
            Assert.That(view.Size.X, Is.LessThanOrEqualTo(520));
        });
        await pair.CleanReturnAsync();
    }
}

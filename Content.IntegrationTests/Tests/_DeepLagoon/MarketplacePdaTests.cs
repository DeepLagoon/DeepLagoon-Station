using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using Content.Client._DeepLagoon.Economy;
using Content.Client._DeepLagoon.WebUI;
using Content.Server._DeepLagoon.Economy;
using Content.Server._NF.Bank;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Shared._DeepLagoon.Economy;
using Content.Shared._DeepLagoon.WebUI;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Access.Components;
using Content.Shared.CartridgeLoader;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.PDA;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class MarketplacePdaTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [Test]
    public async Task PdaReadsAndCommentsUsingInsertedIdButCannotTradeEvenWithForgedActions()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var server = pair.Server; var e = server.EntMan; var map = await pair.CreateTestMap();
        var player = await server.AddDummySession("marketplace_test");
        var prefs = server.ResolveDependency<IServerPreferencesManager>();
        await PoolManager.WaitUntil(server, () => prefs.TryGetCachedPreferences(player.UserId, out _));
        var tasks = server.ResolveDependency<ITaskManager>(); var db = server.ResolveDependency<IServerDbManager>();
        T Complete<T>(Task<T> task) { tasks.BlockWaitOnTask(task); return task.GetAwaiter().GetResult(); }
        void Await(Task task) { tasks.BlockWaitOnTask(task); task.GetAwaiter().GetResult(); }
        var system = e.System<PlayerTradeSystem>();
        EntityUid actor = default, pda = default, card = default; EconomyAccount account = null!; Guid offer = default;
        await server.WaitAssertion(() =>
        {
            server.CfgMan.SetCVar(SectorEconomyCVars.Enabled, true);
            actor = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            e.EnsureComponent<Content.Shared.Mobs.Components.MobStateComponent>(actor);
            e.EnsureComponent<BankAccountComponent>(actor); server.PlayerMan.SetAttachedEntity(player, actor);
            account = Complete(db.ReadEconomyAccountAsync(player.UserId.UserId, prefs.GetPreferences(player.UserId).SelectedCharacterIndex));
            e.System<BankSystem>().SetConfirmedBankBalance(actor, account.Bank);
            pda = e.SpawnEntity("PassengerPDA", map.GridCoords);
            Assert.That(e.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>().TryPickupAnyHand(actor, pda), Is.True);
            var loader = e.GetComponent<CartridgeLoaderComponent>(pda);
            Assert.That(loader.PreinstalledPrograms, Does.Contain("DLMarketplaceCartridge"));
            loader.ActiveProgram = e.SpawnEntity("DLMarketplaceCartridge", map.GridCoords);
            var device = e.GetComponent<PdaComponent>(pda);
            var slots = e.System<ItemSlotsSystem>();
            if (device.ContainedId != null) slots.TryEject(pda, device.IdSlot, null, out _);
            card = e.SpawnEntity("PassengerIDCard", map.GridCoords);
            e.GetComponent<IdCardComponent>(card).FullName = "Имя с ID-карты";
            Assert.That(slots.TryInsert(pda, device.IdSlot, card, null), Is.True);
            e.System<SharedUserInterfaceSystem>().OpenUi(pda, PdaUiKey.Key, actor);
            Assert.That(system.CanBrowseMarketplace(player, pda), Is.True);
            Assert.That(system.MarketplaceAuthor(pda), Is.EqualTo("Имя с ID-карты"));
        });
        await PoolManager.WaitUntil(server, () => system.TradeLifecycleReady);
        await server.WaitAssertion(() =>
        {
            offer = Guid.NewGuid();
            Complete(db.CreateTradeOfferAsync(new(offer, player.UserId.UserId, account.Profile, true, "SheetSteel", "Steel", "", 1, 100, new(), null, null) { Cycle = system.TradeCycleIdentity }, SectorEconomySystem.UtcNow));
        });
        object? access = null;
        async Task Request(TradeAction action, string text = "")
        {
            await server.WaitAssertion(() =>
            {
                ((Dictionary<Robust.Shared.Network.NetUserId, TimeSpan>)typeof(PlayerTradeSystem).GetField("_requests", Private)!.GetValue(system)!).Clear();
                var dict = typeof(PlayerTradeSystem).GetField("_access", Private)!.GetValue(system)!;
                access = dict.GetType().GetMethod("get_Item")!.Invoke(dict, [player.UserId]);
                var token = (Guid)access!.GetType().GetProperty("Token")!.GetValue(access)!;
                typeof(PlayerTradeSystem).GetMethod("OnRequest", Private)!.Invoke(system, [new TradeRequest { Console = e.GetNetEntity(pda), Token = token, Action = action, Offer = offer, Description = text, Price = 1, Prototype = "SheetSteel" }, new EntitySessionEventArgs(player)]);
            });
            await PoolManager.WaitUntil(server, () => !((HashSet<Robust.Shared.Network.NetUserId>)typeof(PlayerTradeSystem).GetField("_reading", Private)!.GetValue(system)!).Contains(player.UserId));
        }
        await server.WaitAssertion(() =>
        {
            Assert.That((bool)typeof(PlayerTradeSystem).GetMethod("CanUse", Private)!.Invoke(system, [player, pda])!, Is.True);
            Assert.That(e.System<BankSystem>().IsCharacter(player, account.Slot), Is.True);
            Await((Task)typeof(PlayerTradeSystem).GetMethod("Board", Private)!.Invoke(system,
                [player, pda, account.Slot, "", 0, TradeFilter.All, TradeSort.Newest, "", "", 0, null, false, offer])!);
        });
        foreach (var action in Enum.GetValues<TradeAction>().Where(a => a is not (TradeAction.Refresh or TradeAction.Comment))) await Request(action);
        await Request(TradeAction.Comment, "<b>Есть в наличии?</b>");
        await server.WaitAssertion(() =>
        {
            var comments = Complete(db.ReadTradeCommentsAsync(offer, SectorEconomySystem.UtcNow));
            Assert.That(comments.Single().Author, Is.EqualTo("Имя с ID-карты"));
            Assert.That(comments.Single().Text, Is.EqualTo("Есть в наличии?"));
            Assert.That(comments.Single().Author, Is.Not.EqualTo(account.Character));
            Assert.That(Complete(db.ReadEconomyAccountAsync(player.UserId.UserId, account.Slot)).Bank, Is.EqualTo(account.Bank - 100));
            Assert.That(Complete(db.ReadTradeBoardAsync(player.UserId.UserId, account.Profile)).Offers.Single().Id, Is.EqualTo(offer));
            e.GetComponent<IdCardComponent>(card).FullName = "Другая карта";
            Assert.That(system.MarketplaceAuthor(pda), Is.EqualTo("Другая карта"));
            e.System<ItemSlotsSystem>().TryEject(pda, e.GetComponent<PdaComponent>(pda).IdSlot, null, out _);
            Assert.That(system.MarketplaceAuthor(pda), Is.Empty);
        });
        await Request(TradeAction.Comment, "Без карты");
        await server.WaitAssertion(() => Assert.That(Complete(db.ReadTradeCommentsAsync(offer, SectorEconomySystem.UtcNow)), Has.Length.EqualTo(1)));
        await pair.CleanReturnAsync();
    }
}

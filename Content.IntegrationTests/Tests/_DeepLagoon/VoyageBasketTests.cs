using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Economy;
using Content.Server._DeepLagoon.Voyages;
using Content.Server._NF.Bank;
using Content.Server._NF.SectorServices;
using Content.Server.Cargo.Components;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Shared._DeepLagoon.Voyages;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Access.Components;
using Content.Shared.Cargo;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Mobs.Components;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class VoyageBasketTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object value, string field) => (T)value.GetType().GetField(field, Private)!.GetValue(value)!;
    private static T Complete<T>(ITaskManager tasks, Task<T> task) { tasks.BlockWaitOnTask(task); return task.GetAwaiter().GetResult(); }

    [TestCase(true)]
    [TestCase(false)]
    public async Task MultiSectorCartQuotesHiresAndCommitsOnceWithRealGoodsAndTreasuryBacking(bool imports)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var server = pair.Server; var e = server.EntMan; var map = await pair.CreateTestMap();
        var player = await server.AddDummySession("cart_captain");
        var prefs = server.ResolveDependency<IServerPreferencesManager>();
        await PoolManager.WaitUntil(server, () => prefs.TryGetCachedPreferences(player.UserId, out _));
        var db = server.ResolveDependency<IServerDbManager>(); var tasks = server.ResolveDependency<ITaskManager>();
        var voyages = e.System<VoyageSystem>();
        EntityUid console = default; EconomyAccount account = null!; SectorBankComponent treasury = null!;
        var orders = new List<StationCargoBountyDatabaseComponent>();
        var budgets = new Dictionary<SectorBankAccount, long>();
        await server.WaitAssertion(() =>
        {
            server.CfgMan.SetCVar(SectorEconomyCVars.Enabled, true);
            server.CfgMan.SetCVar(VoyageCVars.TravelTimeScale, 1f);
            account = Complete(tasks, db.ReadEconomyAccountAsync(player.UserId.UserId, prefs.GetPreferences(player.UserId).SelectedCharacterIndex));
            var actor = e.SpawnEntity("MobHumanDummy", map.GridCoords); e.EnsureComponent<MobStateComponent>(actor); e.EnsureComponent<BankAccountComponent>(actor);
            server.PlayerMan.SetAttachedEntity(player, actor); e.System<BankSystem>().SetConfirmedBankBalance(actor, account.Bank);
            console = e.SpawnEntity(imports ? "ComputerCargoOrders" : "ComputerCargoBounty", map.GridCoords);
            e.RemoveComponent<Content.Server.Power.Components.ApcPowerReceiverComponent>(console);
            e.RemoveComponent<AccessReaderComponent>(console);
            e.System<SharedTransformSystem>().AnchorEntity(console);
            e.System<SharedUserInterfaceSystem>().OpenUi(console, imports ? CargoConsoleUiKey.Orders : CargoConsoleUiKey.Bounty, actor);
            if (imports) return;
            var services = e.System<SectorServiceSystem>();
            if (!e.EntityExists(services.GetServiceEntity())) e.EnsureComponent<StationSectorServiceHostComponent>(e.SpawnEntity(null, map.GridCoords));
            treasury = e.GetComponent<SectorBankComponent>(services.GetServiceEntity());
            var prototype = server.ResolveDependency<IPrototypeManager>().Index<CargoBountyPrototype>("DLEconomyFoodBurger");
            foreach (var payer in new[] { SectorBankAccount.Frontier, SectorBankAccount.Medical })
            {
#pragma warning disable RA0002
                budgets[payer] = e.System<SectorEconomySystem>().PayrollReserve(payer) + 100000;
                treasury.Accounts[payer].Balance = budgets[payer];
#pragma warning restore RA0002
                Assert.That(e.System<BankSystem>().TrySectorWithdraw(payer, 4500, LedgerEntryType.StationWithdrawalBounty), Is.True);
                var station = e.SpawnEntity(null, map.GridCoords);
                var board = e.EnsureComponent<StationCargoBountyDatabaseComponent>(station); board.MaxBounties = 0;
                board.Bounties.Add(new CargoBountyData(prototype, 1) with { FundedReward = 4500, FoodAccount = payer }); orders.Add(board);
            }
            e.System<SharedTransformSystem>().AnchorEntity(e.SpawnEntity("CargoPalletSell", map.GridCoords));
            for (var i = 0; i < 6; i++) e.SpawnEntity("FoodBurgerPlain", map.GridCoords);
        });
        await PoolManager.WaitUntil(server, () => voyages.Ready, 3600);
        VoyageCartLine[] lines = [];
        await server.WaitAssertion(() =>
        {
            if (imports)
            {
                var first = voyages.SectorProducts(console, "helios").OrderBy(p => p.Price).Take(2).ToArray();
                var second = voyages.SectorProducts(console, "nereid").OrderBy(p => p.Price).First();
                Assert.That(first, Has.Length.EqualTo(2));
                lines = [new("helios", first[0].Id, ""), new("helios", first[1].Id, ""), new("nereid", second.Id, "")];
            }
            else lines = voyages.SectorOrderViews().Where(o => o.Destination is "helios" or "nereid").Select(o => new VoyageCartLine(o.Destination, "", o.Id)).ToArray();
        });
        async Task Request(VoyageRequest request)
        {
            await Task.Delay(300);
            await server.WaitAssertion(() => typeof(VoyageSystem).GetMethod("OnRequest", Private)!.Invoke(voyages, [request, new EntitySessionEventArgs(player)]));
            await PoolManager.WaitUntil(server, () => !Field<HashSet<Guid>>(voyages, "_busy").Contains(player.UserId.UserId), 3600);
        }
        var quoteId = Guid.NewGuid();
        await Request(new() { Console = e.GetNetEntity(console), Action = imports ? VoyageAction.QuoteImportCart : VoyageAction.QuoteDeliveryCart, Operation = quoteId, Cart = lines });
        var quotes = Field<IDictionary>(voyages, "_cartQuotes");
        Assert.That(quotes.Contains(player.UserId.UserId), Is.True);
        var quote = quotes[player.UserId.UserId]!;
        var view = (VoyageCartQuoteView)quote.GetType().GetProperty("View")!.GetValue(quote)!;
        Assert.That(view.Routes, Has.Length.EqualTo(2)); Assert.That(view.HireCount, Is.EqualTo(2));
        Assert.That(view.HireCost, Is.Positive);
        Assert.That((await db.ReadVoyagesAsync(player.UserId.UserId, account.Profile)).Bank, Is.EqualTo(account.Bank), "A quote must not charge or hire.");
        if (!imports) Assert.That(view.EstimatedNet, Is.Positive, "Delivery compensation covers cargo, crew amortization/replacement, time and risk.");
        await Request(new() { Console = e.GetNetEntity(console), Action = VoyageAction.ConfirmCart, Quote = quoteId });
        var state = await db.ReadVoyagesAsync(player.UserId.UserId, account.Profile);
        var active = await db.ActiveVoyagesAsync();
        Assert.That(state.Bank, Is.EqualTo(account.Bank - view.HireCost - view.PurchaseCost));
        Assert.That(state.Crews, Has.Length.EqualTo(2)); Assert.That(active, Has.Length.EqualTo(2));
        Assert.That(active.Select(j => j.Crew).Distinct().Count(), Is.EqualTo(2));
        if (imports) Assert.That(active.Single(j => j.Destination == "helios").Cargo, Has.Length.EqualTo(2), "Products from one sector share one cargo flight.");
        else await server.WaitAssertion(() =>
        {
            Assert.That(orders.All(o => o.Bounties.Count == 0), Is.True);
            Assert.That(e.EntityQuery<MetaDataComponent>().Count(m => m.EntityPrototype?.ID == "FoodBurgerPlain"), Is.Zero);
            foreach (var payer in new[] { SectorBankAccount.Frontier, SectorBankAccount.Medical })
                Assert.That(treasury.Accounts[payer].Balance, Is.EqualTo(budgets[payer] - active.Single(j => j.Account == (byte)payer).Reward), "Only the premium above the already reserved food reward is withdrawn now.");
        });
        await Request(new() { Console = e.GetNetEntity(console), Action = VoyageAction.ConfirmCart, Quote = quoteId });
        Assert.That((await db.ReadVoyagesAsync(player.UserId.UserId, account.Profile)).Bank, Is.EqualTo(state.Bank));
        Assert.That(await db.ActiveVoyagesAsync(), Has.Length.EqualTo(2), "Repeated confirmation cannot buy crews or flights twice.");
        if (imports)
        {
            // Another customer buys the quoted crew before confirmation. The whole new cart must roll back.
            var changedId = Guid.NewGuid();
            await Request(new() { Console = e.GetNetEntity(console), Action = VoyageAction.QuoteImportCart, Operation = changedId, Cart = [lines[0]] });
            var changed = quotes[player.UserId.UserId]!;
            var hire = ((VoyageBasketHire[])changed.GetType().GetProperty("Hires")!.GetValue(changed)!).Single();
            var other = await server.AddDummySession("other_crew_customer");
            await PoolManager.WaitUntil(server, () => prefs.TryGetCachedPreferences(other.UserId, out _));
            var otherAccount = await db.ReadEconomyAccountAsync(other.UserId.UserId, prefs.GetPreferences(other.UserId).SelectedCharacterIndex);
            await db.HireVoyageMarketCrewAsync(other.UserId.UserId, otherAccount.Profile, otherAccount.Slot, hire.Offer, Guid.NewGuid());
            await Request(new() { Console = e.GetNetEntity(console), Action = VoyageAction.ConfirmCart, Quote = changedId });
            var after = await db.ReadVoyagesAsync(player.UserId.UserId, account.Profile);
            Assert.That(after.Bank, Is.EqualTo(state.Bank)); Assert.That(after.Crews, Has.Length.EqualTo(2));
            Assert.That(await db.ActiveVoyagesAsync(), Has.Length.EqualTo(2));
            await server.WaitAssertion(() => { Assert.That(e.System<BankSystem>().BeginExchangePayment(player.UserId), Is.True); e.System<BankSystem>().EndExchangePayment(player.UserId); });
        }
        await pair.CleanReturnAsync();
    }
}

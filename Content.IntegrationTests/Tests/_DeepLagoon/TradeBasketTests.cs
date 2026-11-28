using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.CrashRecovery;
using Content.Server._DeepLagoon.Economy;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Shared._DeepLagoon.Economy;
using Content.Shared._NF.Market;
using Content.Shared.Mobs.Components;
using Content.Shared.Stacks;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Content.Server.Cargo.Systems;
using Content.Server.Power.Components;
using Content.Shared.DeviceLinking;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Audio.Components;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class TradeBasketTests
{
    private static T Complete<T>(ITaskManager tasks, Task<T> task) { tasks.BlockWaitOnTask(task); return task.GetAwaiter().GetResult(); }
    private static IDictionary Access(PlayerTradeSystem market) => (IDictionary)typeof(PlayerTradeSystem).GetField("_access", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(market)!;
    private static Guid Token(PlayerTradeSystem market, Robust.Shared.Network.NetUserId user)
        => (Guid)Access(market)[user]!.GetType().GetProperty("Token")!.GetValue(Access(market)[user])!;
    private static IDictionary Quotes(PlayerTradeSystem market) => (IDictionary)typeof(PlayerTradeSystem).GetField("_basketQuotes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(market)!;
    private static IDictionary Shipments(PlayerTradeSystem market) => (IDictionary)typeof(PlayerTradeSystem).GetField("_automaticDeliveries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(market)!;

    [Test]
    public async Task GroupedBoardQuotesMultipleSellersAndDeliversOnlyPurchasedStackUnits()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server; var e = server.EntMan; var map = await pair.CreateTestMap();
        var a = await server.AddDummySession("basket_seller_a"); var b = await server.AddDummySession("basket_seller_b");
        var buyer = pair.Player!;
        var prefs = server.ResolveDependency<IServerPreferencesManager>();
        await PoolManager.WaitUntil(server, () => prefs.TryGetCachedPreferences(a.UserId, out _) && prefs.TryGetCachedPreferences(b.UserId, out _));
        var db = server.ResolveDependency<IServerDbManager>(); var tasks = server.ResolveDependency<ITaskManager>();
        var market = e.System<PlayerTradeSystem>(); var recovery = e.System<CrashRecoverySystem>();
        await server.WaitAssertion(() =>
        {
            server.CfgMan.SetCVar(SectorEconomyCVars.Enabled, true);
            server.CfgMan.SetCVar(RecoveryCVars.Enabled, true);
            server.CfgMan.SetCVar(RecoveryCVars.Scope, "basket" + Guid.NewGuid().ToString("N"));
        });
        await PoolManager.WaitUntil(server, () => market.TradeLifecycleReady && recovery.Ready, 3600);
        EntityUid console = default, telepad = default, body = default;
        var padHold = Guid.NewGuid();
        EconomyAccount account = null!;
        Guid first = default, second = default;
        var fixtures = new System.Collections.Generic.List<TradeItem>();
        void Request(TradeRequest request) => typeof(PlayerTradeSystem).GetMethod("OnRequest", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(market, [request, new EntitySessionEventArgs(buyer)]);
        await server.WaitAssertion(() =>
        {
            EconomyAccount Account(Robust.Shared.Player.ICommonSession session) => Complete(tasks, db.ReadEconomyAccountAsync(session.UserId.UserId, prefs.GetPreferences(session.UserId).SelectedCharacterIndex));
            var sa = Account(a); var sb = Account(b); account = Account(buyer);
            body = e.SpawnEntity("MobHumanDummy", map.GridCoords); e.EnsureComponent<MobStateComponent>(body);
            server.PlayerMan.SetAttachedEntity(buyer, body); recovery.RegisterBody(body, buyer.UserId.UserId, account.Profile, account.Slot);
            e.System<Content.Server._NF.Bank.BankSystem>().SetConfirmedBankBalance(body, account.Bank);
            console = e.SpawnEntity("ComputerMarketConsoleNFNormal", map.GridCoords);
            e.System<SharedTransformSystem>().AnchorEntity(console);
            e.System<SharedUserInterfaceSystem>().OpenUi(console, MarketConsoleUiKey.Default, body);
            e.System<SharedMapSystem>().SetTile(map.Grid, new Vector2i(3, 0), map.Tile.Tile);
            telepad = e.SpawnEntity("CargoTelepad", new EntityCoordinates(map.Grid, 3.5f, .5f));
            e.System<SharedTransformSystem>().AnchorEntity(telepad);
            var power = e.GetComponent<ApcPowerReceiverComponent>(telepad); power.NeedsPower = false; power.Powered = true;
            typeof(Content.Shared.Cargo.Components.CargoTelepadComponent).GetField("Delay")!.SetValue(e.GetComponent<Content.Shared.Cargo.Components.CargoTelepadComponent>(telepad), .05f);
            var capture = typeof(PlayerTradeSystem).GetMethod("Capture", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var bundle = typeof(PlayerTradeSystem).GetMethod("Bundle", BindingFlags.Instance | BindingFlags.NonPublic)!;
            TradeItem Capture(string prototype, int units, Action<EntityUid>? configure = null)
            {
                var root = e.SpawnEntity(prototype, map.GridCoords);
                if (e.TryGetComponent<StackComponent>(root, out var stack)) e.System<SharedStackSystem>().SetCount(root, units, stack);
                configure?.Invoke(root);
                var item = (TradeItem)capture.Invoke(market, [root, bundle.Invoke(market, [root])!])!;
                var diagnosticDirectory = Environment.GetEnvironmentVariable("DL_MARKET_FIXTURE_DIR");
                if (!item.Splittable && prototype == "SheetSteel" && !string.IsNullOrEmpty(diagnosticDirectory))
                {
                    var defaults = server.ResolveDependency<IPrototypeManager>().Index<EntityPrototype>(prototype);
                    var expected = (Content.Shared.Chemistry.Components.SolutionManager.SolutionContainerManagerComponent)defaults.Components["SolutionContainerManager"].Component;
                    var actual = e.System<Content.Shared.Chemistry.EntitySystems.SharedSolutionContainerSystem>().EnumerateSolutions((root, (Content.Shared.Chemistry.Components.SolutionManager.SolutionContainerManagerComponent?)null)).ToArray();
                    Directory.CreateDirectory(diagnosticDirectory);
                    File.WriteAllText(Path.Combine(diagnosticDirectory, "commodity-diagnostic-" + units + ".json"), JsonSerializer.Serialize(new { item, defaultName = defaults.Name,
                        expected = expected.Solutions?.Select(p => new { p.Key, p.Value.Temperature, p.Value.CanReact, contents = p.Value.Contents.Select(r => new { id = r.Reagent.Prototype.Id, quantity = r.Quantity.ToString() }) }),
                        actual = actual.Select(p => new { p.Name, p.Solution.Comp.Solution.Temperature, p.Solution.Comp.Solution.CanReact,
                            contents = p.Solution.Comp.Solution.Contents.Select(r => new { id = r.Reagent.Prototype.Id, quantity = r.Quantity.ToString() }) }),
                        entities = ((EntityUid[])bundle.Invoke(market, [root])!).Select(uid => new { uid, name = e.GetComponent<MetaDataComponent>(uid).EntityName, id = e.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID }) }));
                }
                e.DeleteEntity(root); return item;
            }
            var steel30 = Capture("SheetSteel", 30); var steel50 = Capture("SheetSteel", 50);
            Assert.That(steel30.Splittable, Is.True);
            for (var i = 0; i < 42; i++)
            {
                var seller = i % 2 == 0 ? sa : sb;
                var item = i == 0 ? steel30 : steel50;
                var id = Guid.NewGuid(); if (i == 0) first = id; if (i == 1) second = id;
                Complete(tasks, db.CreateTradeOfferAsync(new(id, i % 2 == 0 ? a.UserId.UserId : b.UserId.UserId, seller.Profile, false,
                    item.Prototype, item.Name, "Seller fixture " + i, 1, i == 0 ? 3000 : i == 1 ? 7000 : 20000 + i * 100, new(), item, null) { Cycle = market.TradeCycleIdentity }, SectorEconomySystem.UtcNow));
            }
            foreach (var id in new[] { "SheetUranium", "SheetGlass", "SheetPlasteel", "SheetPlasma", "MaterialGold", "MaterialSilver", "MaterialCloth", "MaterialDurathread",
                "PowerCellSmall", "PowerCellMedium", "WeaponLaserSvalinn", "Screwdriver", "Wrench", "Crowbar", "Multitool", "CrateGenericSteel", "MetalBarrelGrey", "MicroManipulatorStockPart",
                "MatterBinStockPart", "CapacitorStockPart", "CableApcStack", "CableMVStack", "CableHVStack", "ResearchDisk", "FoodPizzaMargherita", "OxygenTankFilled", "Welder", "AmeJar" })
                if (server.ResolveDependency<IPrototypeManager>().HasIndex<EntityPrototype>(id)) fixtures.Add(Capture(id, 30));
            fixtures.Insert(0, steel30); fixtures.Insert(1, steel50);
            fixtures.Add(Capture("PowerCellSmall", 1, root => e.System<Content.Server.Power.EntitySystems.BatterySystem>().SetCharge(root, 0)));
            fixtures.Add(Capture("SheetSteel", 50, root => e.System<MetaDataSystem>().SetEntityName(root, "Steel collector edition")));
            fixtures.Add(Capture("CrateGenericSteel", 1, root =>
            {
                var disk = e.SpawnEntity("ResearchDisk", map.GridCoords);
                e.GetComponent<Content.Server.Research.Disk.ResearchDiskComponent>(disk).Points = 47382;
                e.System<Robust.Shared.Containers.SharedContainerSystem>().Insert(disk, e.GetComponent<Content.Server.Storage.Components.EntityStorageComponent>(root).Contents);
                e.System<MetaDataSystem>().SetEntityName(root, "Research kit with a custom disk");
            }));
            Assert.That(fixtures.Count, Is.GreaterThanOrEqualTo(22));
            var output = Environment.GetEnvironmentVariable("DL_MARKET_FIXTURE_DIR");
            if (!string.IsNullOrEmpty(output)) { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "items.json"), JsonSerializer.Serialize(fixtures)); }
            Request(new() { Console = e.GetNetEntity(console), Action = TradeAction.Refresh, Group = "sale:SheetSteel" });
        });
        await PoolManager.WaitUntil(server, () => Access(market).Contains(buyer.UserId));
        Assert.That(((IDictionary)Access(market)[buyer.UserId]!.GetType().GetProperty("Owners")!.GetValue(Access(market)[buyer.UserId])!).Count, Is.EqualTo(20));
        await Task.Delay(650);
        var previousToken = Token(market, buyer.UserId);
        await server.WaitAssertion(() => Request(new() { Console = e.GetNetEntity(console), Action = TradeAction.QuoteBasket,
            Token = previousToken, Prototype = "SheetSteel", Quantity = 50, Group = "sale:SheetSteel" }));
        await PoolManager.WaitUntil(server, () => Quotes(market).Contains(buyer.UserId) && Token(market, buyer.UserId) != previousToken);
        var quote = (BasketPlan)Quotes(market)[buyer.UserId]!;
        Assert.That(quote.Rows.Select(r => (r.Offer, r.Units, r.Price)), Is.EqualTo(new[] { (first, 30, 3000L), (second, 20, 2800L) }));
        await Task.Delay(650);
        await server.WaitAssertion(() => Request(new() { Console = e.GetNetEntity(console), Action = TradeAction.PurchaseBasket,
            Token = Token(market, buyer.UserId), Quote = quote.Id, Group = "sale:SheetSteel" }));
        await server.WaitAssertion(() => Assert.That(Complete(tasks, db.ReadTradeBoardAsync(buyer.UserId.UserId, account.Profile)).Bank, Is.EqualTo(account.Bank),
            "An unlinked telepad must not charge the order."));
        await server.WaitAssertion(() =>
        {
            e.System<SharedDeviceLinkSystem>().SaveLinks(null, console, telepad, [("OrderSender", "OrderReceiver")]);
            Assert.That(e.System<CargoSystem>().TryReserveMarketTelepad(console, padHold, out var reserved), Is.True);
            Assert.That(reserved, Is.EqualTo(telepad));
        });
        await Task.Delay(650);
        await server.WaitAssertion(() => Request(new() { Console = e.GetNetEntity(console), Action = TradeAction.PurchaseBasket,
            Token = Token(market, buyer.UserId), Quote = quote.Id, Group = "sale:SheetSteel" }));
        TradeBoard board = null!;
        var purchaseSound = false; var teleportSound = false;
        await PoolManager.WaitUntil(server, async () =>
        {
            await server.WaitAssertion(() => purchaseSound |= e.EntityQuery<AudioComponent>().Any(a => a.FileName == "/Audio/Effects/Cargo/ping.ogg"));
            board = await db.ReadTradeBoardAsync(buyer.UserId.UserId, account.Profile);
            return board.Deliveries.Length == 2 && Access(market).Contains(buyer.UserId);
        });
        Assert.That(board.Bank, Is.EqualTo(account.Bank - 5800));
        await server.WaitAssertion(() =>
        {
            var seller = Complete(tasks, db.ReadEconomyAccountAsync(a.UserId.UserId, prefs.GetPreferences(a.UserId).SelectedCharacterIndex));
            var notices = Complete(tasks, db.ReadCharacterNotificationsAsync(a.UserId.UserId, seller.Profile, false));
            Assert.That(notices, Has.Length.EqualTo(1), "A committed multi-seller purchase writes one durable notice for this seller.");
            Assert.That(notices[0].Amount, Is.EqualTo(3000));
            Assert.That(notices[0].Text, Does.Contain("×30"));
            Complete(tasks, db.ReadCharacterNotificationsAsync(a.UserId.UserId, seller.Profile, true));
            Assert.That(Complete(tasks, db.ReadCharacterNotificationsAsync(a.UserId.UserId, seller.Profile, false)).All(n => n.Read), Is.True);
            Assert.That(Complete(tasks, db.ReadCharacterNotificationsAsync(buyer.UserId.UserId, account.Profile, false)), Is.Empty,
                "The buyer must not receive the sellers' private sale notifications.");
        });
        await server.WaitAssertion(() =>
        {
            Assert.That(purchaseSound, Is.True);
            var power = e.GetComponent<ApcPowerReceiverComponent>(telepad); power.NeedsPower = true; power.Powered = false;
            e.System<CargoSystem>().ReleaseMarketTelepad(telepad, padHold, false);
        });
        await server.WaitRunTicks(10);
        await server.WaitAssertion(() =>
        {
            Assert.That(Complete(tasks, db.ReadTradeBoardAsync(buyer.UserId.UserId, account.Profile)).Deliveries, Has.Length.EqualTo(2));
            var power = e.GetComponent<ApcPowerReceiverComponent>(telepad); power.NeedsPower = false; power.Powered = true;
        });
        await PoolManager.WaitUntil(server, async () =>
        {
            await server.WaitAssertion(() => teleportSound |= e.EntityQuery<AudioComponent>().Any(a => a.FileName == "/Audio/Machines/phasein.ogg"));
            return (await db.ReadTradeBoardAsync(buyer.UserId.UserId, account.Profile)).Deliveries.Length == 0 && Shipments(market).Count == 0;
        });
        await server.WaitAssertion(() =>
        {
            var delivered = e.EntityQuery<StackComponent>().Where(s => e.GetComponent<MetaDataComponent>(s.Owner).EntityPrototype?.ID == "SheetSteel").ToArray();
            Assert.That(delivered.Select(s => s.Count).Order(), Is.EqualTo(new[] { 20, 30 }));
            foreach (var item in delivered) Assert.That(e.GetComponent<TransformComponent>(item.Owner).Coordinates, Is.EqualTo(e.GetComponent<TransformComponent>(telepad).Coordinates));
            Assert.That(teleportSound, Is.True);
        });
        await server.WaitRunTicks(10);
        await server.WaitAssertion(() =>
        {
            // The existing local-stock market must use the same linked telepad instead of searching for a crate machine.
            var component = e.GetComponent<Content.Server._NF.Market.Components.MarketConsoleComponent>(console);
            typeof(Content.Server._NF.Market.Components.MarketConsoleComponent).GetField("CartDataList")!.SetValue(component,
                new System.Collections.Generic.List<Content.Server._NF.Market.MarketLot> { new() { Prototype = "Wrench", Quantity = 1, Price = 100 } });
            var balance = e.GetComponent<Content.Shared._NF.Bank.Components.BankAccountComponent>(body).Balance;
            var power = e.GetComponent<ApcPowerReceiverComponent>(telepad); power.NeedsPower = true; power.Powered = false;
            e.EventBus.RaiseLocalEvent(console, new Content.Shared._NF.Market.Events.MarketPurchaseMessage { Actor = body });
            Assert.That(e.GetComponent<Content.Shared._NF.Bank.Components.BankAccountComponent>(body).Balance, Is.EqualTo(balance));
            power.NeedsPower = false; power.Powered = true;
            e.EventBus.RaiseLocalEvent(console, new Content.Shared._NF.Market.Events.MarketPurchaseMessage { Actor = body });
            Assert.That(e.GetComponent<Content.Shared._NF.Bank.Components.BankAccountComponent>(body).Balance, Is.EqualTo(balance - 800));
            var crate = e.EntityQuery<Content.Server.Storage.Components.EntityStorageComponent>()
                .Single(s => e.GetComponent<MetaDataComponent>(s.Owner).EntityPrototype?.ID == "CrateGenericSteel");
            Assert.That(e.GetComponent<TransformComponent>(crate.Owner).Coordinates, Is.EqualTo(e.GetComponent<TransformComponent>(telepad).Coordinates));
            Assert.That(crate.Contents.ContainedEntities.Any(uid => e.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "Wrench"), Is.True);
        });
        await PoolManager.WaitUntil(server, async () => (await db.ReadEconomyAccountAsync(buyer.UserId.UserId, account.Slot)).Bank == account.Bank - 6600);
        await Task.Delay(650);
        await server.WaitAssertion(() => Request(new() { Console = e.GetNetEntity(console), Action = TradeAction.Deal,
            Token = Token(market, buyer.UserId), Offer = second, Group = "sale:SheetSteel" }));
        await PoolManager.WaitUntil(server, async () =>
        {
            var current = await db.ReadTradeBoardAsync(buyer.UserId.UserId, account.Profile);
            return current.Bank == account.Bank - 10800 && current.Deliveries.Length == 0 && Shipments(market).Count == 0 && Access(market).Contains(buyer.UserId);
        });
        await server.WaitAssertion(() =>
        {
            var delivered = e.EntityQuery<StackComponent>().Where(s => e.GetComponent<MetaDataComponent>(s.Owner).EntityPrototype?.ID == "SheetSteel").ToArray();
            Assert.That(delivered.Select(s => s.Count).Order(), Is.EqualTo(new[] { 20, 30, 30 }));
            foreach (var item in delivered) Assert.That(e.GetComponent<TransformComponent>(item.Owner).Coordinates, Is.EqualTo(e.GetComponent<TransformComponent>(telepad).Coordinates));
        });
        Guid returned = default;
        await server.WaitAssertion(() =>
        {
            var item = fixtures.First(i => i.Prototype == "Wrench"); var operation = Guid.NewGuid();
            Complete(tasks, db.CreateTradeOfferAsync(new(operation, buyer.UserId.UserId, account.Profile, false, item.Prototype, item.Name, "Return fixture", 1, 10, new(), item, null)
                { Cycle = market.TradeCycleIdentity }, SectorEconomySystem.UtcNow));
            returned = Complete(tasks, db.CancelTradeOfferAsync(buyer.UserId.UserId, account.Profile, operation, Guid.NewGuid(), SectorEconomySystem.UtcNow)).Delivery!.Value;
        });
        await server.WaitRunTicks(10); await Task.Delay(650);
        await server.WaitAssertion(() => Request(new() { Console = e.GetNetEntity(console), Action = TradeAction.Claim,
            Token = Token(market, buyer.UserId), Offer = returned, Group = "sale:SheetSteel" }));
        await PoolManager.WaitUntil(server, async () => (await db.ReadTradeBoardAsync(buyer.UserId.UserId, account.Profile)).Deliveries.Length == 0 &&
            !e.System<Content.Server._NF.Bank.BankSystem>().IsPaymentPending(buyer.UserId));
        await server.WaitAssertion(() =>
        {
            Assert.That(e.EntityQuery<TransformComponent>().Any(t => t.Coordinates == e.GetComponent<TransformComponent>(telepad).Coordinates &&
                e.GetComponent<MetaDataComponent>(t.Owner).EntityPrototype?.ID == "Wrench"), Is.True);
        });
        await recovery.StopWorker();
        await pair.CleanReturnAsync();
    }
}

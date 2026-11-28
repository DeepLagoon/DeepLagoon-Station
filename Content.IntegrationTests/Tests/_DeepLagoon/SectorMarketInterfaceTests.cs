using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Client._DeepLagoon.Economy;
using Content.Client._DeepLagoon.WebUI;
using Content.Server._DeepLagoon.Economy;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Economy;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class SectorMarketInterfaceTests
{
    [Test]
    public async Task GroupedBasketKeepsSellerSelectionAndRequiresTheServerTotalBeforePurchase()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>(); var style = cfg.GetCVar(WebUiCVars.WindowStyles);
            cfg.SetCVar(WebUiCVars.WindowStyles, WindowInterfaceStyle.WithChoice(style, "SectorMarket", false));
            try
            {
                var sent = new List<TradeRequest>(); using var window = new TradeWindow(sent.Add); window.OpenCentered();
                var a = new TradeOfferView(Guid.NewGuid(), false, false, "Alice", "Steel", "<b>Clean description</b>", 3000, 1, 100, 0, 30, long.MaxValue)
                    { Prototype = "SheetSteel", Splittable = true, OriginalPrice = 3000, OriginalUnits = 30 };
                var b = a with { Id = Guid.NewGuid(), Character = "Bob", Price = 7000, StackUnits = 50, OriginalPrice = 7000, OriginalUnits = 50 };
                var group = new TradeGroupView("sale:SheetSteel", "SheetSteel", "Steel", false, 3000, 30);
                TradeSnapshot State(TradeQuoteView? quote = null) => new() { Token = Guid.NewGuid(), Status = "ready", Bank = 20000,
                    Group = group.Key, Groups = [group], Offers = [a, b], Pages = 1, SellerPages = 1, Quote = quote };
                window.Update(State());
                var catalogPane = (BoxContainer)typeof(TradeWindow).GetField("_catalogPane", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var offersPane = (BoxContainer)typeof(TradeWindow).GetField("_offersPane", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.That(catalogPane.Visible, Is.True); Assert.That(offersPane.Visible, Is.False);
                foreach (var width in new[] { 1100, 900 })
                foreach (var layout in new[] { "cards", "list" })
                {
                    Act(window, "layout", "{\"value\":\"" + layout + "\"}");
                    var size = new Vector2(width, 760); window.SetSize = size; window.Measure(size); window.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
                    Assert.That(catalogPane.Height, Is.GreaterThan(300));
                    Assert.That(catalogPane.Width, Is.GreaterThan(width - 90), "Closed offers must reserve no right preview column.");
                }
                Act(window, "group", "{\"key\":\"" + group.Key + "\"}");
                Assert.That(offersPane.Visible, Is.True); Assert.That(catalogPane.Visible, Is.False);
                Act(window, "closeGroup"); window.Update(State());
                Assert.That(offersPane.Visible, Is.False, "A delayed refresh must not reopen a closed group.");
                Assert.That(catalogPane.Visible, Is.True);
                Act(window, "group", "{\"key\":\"" + group.Key + "\"}");
                var clean = (TradeSnapshot)typeof(TradeWindow).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.That(clean.Offers[0].Description, Is.EqualTo("Clean description"));
                Act(window, "select", "{\"id\":\"" + a.Id + "\"}"); Act(window, "basketAdd");
                Act(window, "select", "{\"id\":\"" + b.Id + "\"}"); Act(window, "lotAmount", "{\"value\":\"20\"}"); Act(window, "basketAdd");
                Assert.That(sent.Any(r => r.Action == TradeAction.PurchaseBasket), Is.False);
                Act(window, "basketQuote");
                var outgoing = typeof(TradeWindow).GetField("_outgoing", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var request = (TradeRequest)outgoing.GetValue(window)!;
                Assert.That(request.Action, Is.EqualTo(TradeAction.QuoteBasket));
                Assert.That(request.Lines.Select(l => (l.Offer, l.Units)), Is.EqualTo(new[] { (a.Id, 30), (b.Id, 20) }));
                outgoing.SetValue(window, null); // The server response below acknowledges this queued request.
                var quote = new TradeQuoteView(Guid.NewGuid(), 5800, [new(a.Id, a.Prototype, a.Name, a.Character, 30, 3000), new(b.Id, b.Prototype, b.Name, b.Character, 20, 2800)]);
                window.Update(State(quote));
                var panel = (TguiPanel)typeof(TradeWindow).GetField("_panel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                panel.SwitchToTgui(); panel.SwitchToNative();
                Assert.That(outgoing.GetValue(window), Is.Null, "Presentation changes never accept the quote.");
                Act(window, "basketConfirm"); request = (TradeRequest)outgoing.GetValue(window)!;
                Assert.That(request.Action, Is.EqualTo(TradeAction.PurchaseBasket)); Assert.That(request.Quote, Is.EqualTo(quote.Id));
                outgoing.SetValue(window, null);
                window.Update(new() { Status = "basket_changed" });
                Act(window, "basketConfirm"); Assert.That(outgoing.GetValue(window), Is.Null, "A rejected quote must be calculated again.");
            }
            finally { cfg.SetCVar(WebUiCVars.WindowStyles, style); }
        });
        await pair.CleanReturnAsync();
    }

    private static void Act(TradeWindow window, string action, string payload = "{}") => typeof(TradeWindow)
        .GetMethod("OnWebAction", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [action, payload]);

    [Test]
    public async Task LocalListingsKeepServerCartMessagesAndRequireFreshCheckoutConfirmation()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            var style = cfg.GetCVar(WebUiCVars.WindowStyles);
            cfg.SetCVar(WebUiCVars.WindowStyles, WindowInterfaceStyle.WithChoice(style, "SectorMarket", false));
            try
            {
                var sent = new List<BoundUserInterfaceMessage>();
                using var window = new TradeWindow(_ => { });
                window.ConfigureLocalMarket(sent.Add);
                Content.Shared._NF.Market.BUI.MarketConsoleInterfaceState State(long cost = 400, bool hasCart = true) => new(10000, 2,
                    [new("SheetSteel", "Steel", 45, 40)], hasCart ? [new("SheetSteel", "Steel", 5, 40)] : [], cost, true, 600, hasCart ? 1 : 0);
                window.UpdateLocalMarket(State(0, false));
                Act(window, "section", "{\"value\":\"local\"}");
                Act(window, "localAdd", "{\"prototype\":\"SheetSteel\",\"amount\":5}");
                Act(window, "localAdd", "{\"prototype\":\"SheetSteel\",\"amount\":5}");
                Assert.That(sent.Count, Is.EqualTo(1), "The second click waits for the authoritative cart update.");
                var add = (Content.Shared._NF.Market.Events.MarketConsoleCartMessage)sent.Single();
                Assert.That(add.Amount, Is.EqualTo(5)); Assert.That(add.ItemPrototype, Is.EqualTo("SheetSteel"));
                window.UpdateLocalMarket(State());
                var serialize = typeof(TradeWindow).GetMethod("LocalData", BindingFlags.Instance | BindingFlags.NonPublic)!;
                using var data = System.Text.Json.JsonDocument.Parse(serialize.Invoke(window, [])!.ToString()!);
                Assert.That(data.RootElement.GetProperty("total").GetString(), Does.Contain("1000"));
                Assert.That(Descendants(window).OfType<TradeItemPreview>().Any(), Is.True);
                Act(window, "localCheckout");
                Assert.That(sent.Count, Is.EqualTo(1));
                window.UpdateLocalMarket(State(450));
                Act(window, "localConfirm");
                Assert.That(sent.Count, Is.EqualTo(1), "A price change invalidates the old confirmation.");
                Act(window, "localCheckout"); Act(window, "localConfirm"); Act(window, "localConfirm");
                Assert.That(sent.OfType<Content.Shared._NF.Market.Events.MarketPurchaseMessage>().Count(), Is.EqualTo(1));
                var panel = (TguiPanel)typeof(TradeWindow).GetField("_panel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                panel.SwitchToTgui(); panel.SwitchToNative();
                Assert.That(sent.Count, Is.EqualTo(2), "Switching presentation must never repeat a purchase.");
            }
            finally { cfg.SetCVar(WebUiCVars.WindowStyles, style); }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BatteryMetadataDistinguishesNoCellFromAnEmptyCellAndHidesIrrelevantUiFields()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan; var market = e.System<PlayerTradeSystem>();
            var describe = typeof(PlayerTradeSystem).GetMethod("Describe", BindingFlags.Instance | BindingFlags.NonPublic)!;
            TradeItem Read(EntityUid entity) => (TradeItem)describe.Invoke(market, [entity])!;
            Assert.That(Read(e.SpawnEntity("MetalBarrelGrey", map.GridCoords)).HasBattery, Is.False);
            var cell = e.SpawnEntity("PowerCellSmall", map.GridCoords);
            e.System<Content.Server.Power.EntitySystems.BatterySystem>().SetCharge(cell, 0);
            Assert.That(Read(cell).HasBattery, Is.True); Assert.That(Read(cell).Charge, Is.Zero);
            var gun = e.SpawnEntity("WeaponLaserSvalinn", map.GridCoords);
            Assert.That(Read(gun).HasBattery, Is.True);
            var slot = e.GetComponent<Content.Shared.PowerCell.Components.PowerCellSlotComponent>(gun);
            var containers = e.System<Robust.Shared.Containers.SharedContainerSystem>();
            containers.EmptyContainer(containers.GetContainer(gun, slot.CellSlotId), force: true);
            Assert.That(Read(gun).HasBattery, Is.False, "A removable battery slot is not itself a battery.");
            var saved = System.Text.Json.JsonSerializer.Serialize(Read(cell));
            Assert.That(System.Text.Json.JsonSerializer.Deserialize<TradeItem>(saved)!.HasBattery, Is.True);
        });
        await pair.Client.WaitAssertion(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            var style = cfg.GetCVar(WebUiCVars.WindowStyles);
            cfg.SetCVar(WebUiCVars.WindowStyles, WindowInterfaceStyle.WithChoice(style, "SectorMarket", false));
            try
            {
                using var window = new TradeWindow(_ => { });
                var barrel = new TradeOfferView(Guid.NewGuid(), false, false, "Seller", "Barrel", "", 100, 1, 100, 0, 1, long.MaxValue) { Prototype = "MetalBarrelGrey" };
                var empty = barrel with { Id = Guid.NewGuid(), Name = "Empty cell", Prototype = "PowerCellSmall", HasBattery = true };
                var noCell = barrel with { Id = Guid.NewGuid(), Name = "Gun with empty slot", Prototype = "WeaponLaserSvalinn", HasBattery = false };
                window.Update(new() { Token = Guid.NewGuid(), Status = "ready", Offers = [barrel, empty, noCell] });
                var serialize = typeof(TradeWindow).GetMethod("OfferData", BindingFlags.Instance | BindingFlags.NonPublic)!;
                bool Has(TradeOfferView offer)
                {
                    using var json = System.Text.Json.JsonDocument.Parse(serialize.Invoke(window, [offer])!.ToString()!);
                    return json.RootElement.GetProperty("hasBattery").GetBoolean();
                }
                Assert.That(Has(barrel), Is.False, "Existing barrel listings without the new metadata must stay compatible.");
                Assert.That(Has(empty), Is.True); Assert.That(Has(noCell), Is.False);
                var detail = (BoxContainer)typeof(TradeWindow).GetField("_detail", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                string Detail() => string.Join(" ", detail.Children.OfType<RichTextLabel>().Select(l => l.Text));
                var charge = pair.Client.ResolveDependency<Robust.Shared.Localization.ILocalizationManager>().GetString("dl-trade-battery");
                Assert.That(Detail().Contains(charge, StringComparison.OrdinalIgnoreCase), Is.False);
                Act(window, "select", "{\"id\":\"" + empty.Id + "\"}");
                Assert.That(Detail().Contains(charge, StringComparison.OrdinalIgnoreCase), Is.True);
                Assert.That(Detail(), Does.Contain("0%"));
                Act(window, "select", "{\"id\":\"" + noCell.Id + "\"}");
                Assert.That(Detail().Contains(charge, StringComparison.OrdinalIgnoreCase), Is.False);
            }
            finally { cfg.SetCVar(WebUiCVars.WindowStyles, style); }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NativeMarketDetailsAndPreviewStayInsideTheWindowAtDefaultAndMinimumWidths()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            var style = cfg.GetCVar(WebUiCVars.WindowStyles); var cards = cfg.GetCVar(WebUiCVars.MarketCards);
            cfg.SetCVar(WebUiCVars.WindowStyles, WindowInterfaceStyle.WithChoice(style, "SectorMarket", false));
            try
            {
                using var window = new TradeWindow(_ => { });
                var name = "A renamed cargo container with a long descriptive name";
                var description = string.Join(" ", Enumerable.Repeat("A detailed description of the cargo and its condition.", 8));
                var id = Guid.NewGuid();
                window.Update(new TradeSnapshot { Token = Guid.NewGuid(), Enabled = true, Status = "ready", Bank = 4000, Pages = 1, Total = 2,
                    Offers = [new TradeOfferView(id, true, false, "A character with a long name", name, description, 1000, 1, 100, 0, 1, long.MaxValue) { Prototype = "CrateGenericSteel" },
                        new TradeOfferView(Guid.NewGuid(), false, false, "Seller", name, description, 1000, 1, 100, 0, 1, long.MaxValue) { Prototype = "CrateGenericSteel" }] });
                window.OpenCentered();
                var detail = (BoxContainer)typeof(TradeWindow).GetField("_detail", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var viewport = (ScrollContainer)detail.Parent!;
                foreach (var width in new[] { 1100, 900 })
                foreach (var mode in new[] { "cards", "list" })
                {
                    Act(window, "layout", "{\"value\":\"" + mode + "\"}");
                    window.SetSize = new Vector2(width, 600);
                    for (var pass = 0; pass < 3; pass++)
                    {
                        window.Measure(new Vector2(width, 600));
                        window.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(width, 600)));
                    }
                    Assert.That(viewport.GlobalPosition.X + viewport.Width, Is.LessThanOrEqualTo(window.GlobalPosition.X + window.Width + 1), $"{width}/{mode}: details viewport escaped window.");
                    Assert.That(detail.Width, Is.LessThanOrEqualTo(viewport.Width + 1), $"{width}/{mode}: content must wrap to the viewport rather than grow behind clipping.");
                    foreach (var control in detail.Children)
                        Assert.That(control.GlobalPosition.X + control.Width, Is.LessThanOrEqualTo(viewport.GlobalPosition.X + viewport.Width + 1), $"{width}/{mode}: {control.GetType().Name} escaped the right edge.");
                    Assert.That(viewport.HScrollEnabled, Is.False);
                }
            }
            finally { cfg.SetCVar(WebUiCVars.WindowStyles, style); cfg.SetCVar(WebUiCVars.MarketCards, cards); }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LayoutSwitchPreservesSelectionAndDraftAndPreviewsNeverSpawnGameplayEntities()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            var style = cfg.GetCVar(WebUiCVars.WindowStyles); var cards = cfg.GetCVar(WebUiCVars.MarketCards);
            cfg.SetCVar(WebUiCVars.WindowStyles, WindowInterfaceStyle.WithChoice(style, "SectorMarket", false));
            var e = pair.Client.EntMan;
            var count = e.EntityQuery<MetaDataComponent>(true).Count();
            var requests = new List<TradeRequest>();
            using var window = new TradeWindow(requests.Add);
            try
            {
                var id = Guid.NewGuid(); var token = Guid.NewGuid();
                window.Update(new TradeSnapshot { Token = token, Enabled = true, Status = "ready", Bank = 4000, Pages = 1, Total = 1,
                    Offers = [new TradeOfferView(id, false, false, "Seller", "Steel", "A whole stack", 1000, 1, 100, 0, 50, long.MaxValue) { Prototype = "SheetSteel", Category = "materials" }],
                    Items = [new(default, "Steel", "SheetSteel", 100, 0, 50)], Deliveries = [new(Guid.NewGuid(), "Crate") { Prototype = "CrateGenericSteel" }] });
                window.OpenCentered();
                window.Measure(new Vector2(1100, 760)); window.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(1100, 760)));
                var previews = Descendants(window).OfType<TradeItemPreview>().ToArray();
                Assert.That(previews.Any(p => p.LayerCount > 0), Is.True, "Native previews must use real resource textures.");
                Assert.That(e.EntityQuery<MetaDataComponent>(true).Count(), Is.EqualTo(count), "Showing market items must not start their gameplay systems.");
                Act(window, "price", "{\"value\":\"1700\"}");
                Act(window, "description", "{\"value\":\"Saved draft\"}");
                Act(window, "layout", "{\"value\":\"list\"}");
                Act(window, "layout", "{\"value\":\"cards\"}");
                Assert.That(requests, Is.Empty, "Presentation changes cannot buy, sell, claim or refresh.");
                Assert.That(cfg.GetCVar(WebUiCVars.MarketCards), Is.True);
                var price = (LineEdit)typeof(TradeWindow).GetField("_price", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.That(price.Text, Is.EqualTo("1700"));
                Assert.That(typeof(TradeWindow).GetField("_selected", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window), Is.EqualTo(id));
                Act(window, "confirm");
                Assert.That(typeof(TradeWindow).GetField("_outgoing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window), Is.Null);
                Act(window, "prepare", "{\"id\":\"" + id + "\"}");
                Assert.That(typeof(TradeWindow).GetField("_outgoing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window), Is.Null);
                Act(window, "confirm");
                var outgoing = (TradeRequest)typeof(TradeWindow).GetField("_outgoing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.That(outgoing.Action, Is.EqualTo(TradeAction.Deal)); Assert.That(outgoing.Offer, Is.EqualTo(id));
                var refresh = (TradeSnapshot)typeof(TradeWindow).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                window.Update(refresh);
                Assert.That(typeof(TradeWindow).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window), Is.EqualTo(true),
                    "A refresh reply must not unlock an already queued financial operation.");
                Act(window, "confirm");
                Assert.That(typeof(TradeWindow).GetField("_outgoing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window), Is.SameAs(outgoing));
            }
            finally { cfg.SetCVar(WebUiCVars.WindowStyles, style); cfg.SetCVar(WebUiCVars.MarketCards, cards); }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MarketFiltersAndPriceSortingApplyToTheWholeBoardBeforePaginationAndKeepCharacterPrivacy()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Server.WaitAssertion(() =>
        {
            var market = pair.Server.EntMan.System<PlayerTradeSystem>();
            var user = Guid.NewGuid(); var other = Guid.NewGuid();
            var source = Enumerable.Range(0, 65).Select(i => new TradeOffer { Id = Guid.NewGuid(), User = other, Profile = 1,
                Prototype = "SheetSteel", ItemName = "Renamed stack", Price = i + 1, Created = i, Expires = long.MaxValue }).ToList();
            source.Add(new() { Id = Guid.NewGuid(), User = user, Profile = 7, Prototype = "SheetSteel", ItemName = "Private to another slot", Price = 0, Expires = long.MaxValue });
            source.Add(new() { Id = Guid.NewGuid(), User = user, Profile = 2, Prototype = "SheetSteel", ItemName = "My order", BuyOrder = true, Price = 10, Expires = long.MaxValue });
            var sorted = market.SelectOffers(source, user, 2, "", TradeFilter.Sale, TradeSort.MostExpensive, "materials");
            Assert.That(sorted, Has.Length.EqualTo(65));
            Assert.That(sorted.Take(40).Select(o => o.Price), Is.EqualTo(Enumerable.Range(26, 40).Reverse().Select(i => (long)i)));
            Assert.That(market.SelectOffers(source, user, 2, "", TradeFilter.Mine, TradeSort.Newest, "").Single().ItemName, Is.EqualTo("My order"));
            Assert.That(market.SelectOffers(source, user, 2, "", TradeFilter.Buy, TradeSort.Cheapest, "").Single().BuyOrder, Is.True);
            Assert.That(market.SelectOffers(source, user, 2, "", TradeFilter.All, TradeSort.Newest, "food"), Is.Empty);
        });
        await pair.CleanReturnAsync();
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (var child in root.Children) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
}

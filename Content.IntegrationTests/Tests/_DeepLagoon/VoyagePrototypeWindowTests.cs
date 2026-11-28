using Content.Shared._DeepLagoon.Voyages;
using Content.Client.Cargo.UI;
using Robust.Shared.GameObjects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Client._DeepLagoon.Economy;
using Content.Client._DeepLagoon.Voyages;
using Content.Shared._FarHorizons.StarSystem.Prototypes;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class VoyagePrototypeWindowTests
{
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (var child in root.Children) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    [Test]
    public async Task NativePrototypeUsesRealAssetsAndClickingASystemOpensItsOrdersAtBothWindowSizes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        VoyagePrototypeWindow window = null!;
        CargoBountyMenu console = null!;
        await pair.Client.WaitAssertion(() =>
        {
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var cargo = new CargoBountyMenu(); cargo.OpenCentered();
            Assert.That(Descendants(cargo).OfType<Button>().Any(b => b.Text == "Карта рейсов"), Is.True);
            cargo.Close(); cargo.Dispose();
            window = new VoyagePrototypeWindow(); window.OpenCentered();
            foreach (var system in window.Model.Systems)
            {
                Assert.That(prototypes.HasIndex<PlanetTypePrototype>(system.PlanetType), Is.True);
                foreach (var order in system.Orders) Assert.That(prototypes.HasIndex<EntityPrototype>(order.Prototype), Is.True, order.Prototype);
            }
            foreach (var width in new[] { 920, 1240 })
            {
                var size = new Vector2(width, 790); window.SetSize = size;
                window.Measure(size); window.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
                Assert.That(window.Map.Width, Is.GreaterThan(width * .52));
                var sidebar = Descendants(window).Single(c => c.Name == "VoyageSidebar");
                Assert.That(sidebar.Width, Is.EqualTo(360).Within(1));
                Assert.That(sidebar.GlobalPosition.X - (window.Map.GlobalPosition.X + window.Map.Width), Is.InRange(0, 12));
                Assert.That(sidebar.GlobalPosition.X + sidebar.Width, Is.GreaterThan(window.GlobalPosition.X + window.Width - 25));
                Assert.That(window.Map.GlobalPosition.X + window.Map.Width, Is.LessThanOrEqualTo(window.GlobalPosition.X + window.Width));
                foreach (var label in Descendants(window).OfType<Label>().Where(c => c.VisibleInTree && c.Size.X > 0))
                    Assert.That(label.GlobalPosition.X + label.Width, Is.LessThanOrEqualTo(window.GlobalPosition.X + window.Width + 1));
            }
            Assert.That(Descendants(window).OfType<TradeItemPreview>().Any(p => p.LayerCount > 0), Is.True);
        });
        await pair.Client.WaitRunTicks(2);
        async Task Click(Button button)
        {
            var point = new ScreenCoordinates(button.GlobalPixelPosition + button.PixelSize / 2, button.Window!.Id);
            foreach (var state in new[] { BoundKeyState.Down, BoundKeyState.Up })
                await pair.Client.DoGuiEvent(button, new GUIBoundKeyEventArgs(EngineKeyFunctions.UIClick, state, point, false, Vector2.Zero, Vector2.Zero));
        }
        Button hireTab = null!, hire = null!;
        await pair.Client.WaitAssertion(() => hireTab = Descendants(window).OfType<Button>().Single(b => b.Text == "Нанять экипаж"));
        await Click(hireTab);
        await pair.Client.WaitAssertion(() => hire = Descendants(window).OfType<Button>().Single(b => b.Text?.StartsWith("Нанять · 6") == true));
        await Click(hire);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(window.Model.Crews.Count, Is.EqualTo(1));
            Assert.That(window.Model.HiringBudget, Is.EqualTo(144000));
            var badge = Descendants(window.Map).Single(c => c.Name == "Demand-colossus");
            var center = window.Map.ScreenPosition(window.Model.System(VoyagePrototype.Home).Position) / window.Map.UIScale;
            var label = Descendants(window.Map).Single(c => c.Name == "SystemLabel-colossus");
            Assert.That(badge.GlobalPosition.Y, Is.GreaterThanOrEqualTo(label.GlobalPosition.Y + label.Height + 7));
            Assert.That(badge.Height, Is.EqualTo(40).Within(1));
            Assert.That(Descendants(badge).OfType<TradeItemPreview>().All(p => p.Width >= 36 && p.Height >= 36), Is.True);
        });
        var map = window.Map;
        var pixel = map.ScreenPosition(window.Model.System("nereid").Position);
        var screen = new ScreenCoordinates(map.GlobalPixelPosition + pixel, map.Window!.Id);
        await pair.Client.DoGuiEvent(map, new GUIBoundKeyEventArgs(EngineKeyFunctions.UIClick, BoundKeyState.Down, screen,
            false, pixel / map.UIScale, pixel));
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(map.Selected, Is.EqualTo("nereid"));
            Assert.That(Descendants(window).OfType<Label>().Any(label => label.Text == "Нереида"), Is.True);
            Assert.That(Descendants(window).OfType<Button>().Count(button => button.Text == "Выбрать заказ"), Is.EqualTo(2));
            window.SelectSystem("echo");
            Assert.That(Descendants(window).OfType<Button>().Any(button => button.Text == "Отправить разведку" && !button.Disabled), Is.True);
            Assert.That(Descendants(window).OfType<Button>().Any(button => button.Text == "Выбрать заказ"), Is.False);
        });
        Button dispatch = null!;
        await pair.Client.WaitAssertion(() => dispatch = Descendants(window).OfType<Button>().Single(b => b.Text == "Отправить разведку"));
        await Click(dispatch);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(window.Model.IsBusy(window.Model.Crews.Single()), Is.True);
            window.Close(); window.Dispose();
            console = new CargoBountyMenu(); console.OnVoyageMapPressed += () => pair.Client.ResolveDependency<IEntityManager>().System<VoyagePrototypeUiSystem>().Open(); console.OpenCentered();
            hireTab = Descendants(console).OfType<Button>().Single(b => b.Text == "Карта рейсов");
        });
        await Click(hireTab);
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IEntityManager>().System<VoyagePrototypeUiSystem>();
            Assert.That(ui.Window?.IsOpen, Is.True);
            ui.Window!.Model.Hire("cargo"); ui.Window.Close(); ui.Open();
            Assert.That(ui.Window.Model.Crews.Count, Is.EqualTo(1));
            ui.Window.Close(); console.Close(); console.Dispose();
            var live = new VoyagePrototypeWindow(new NetEntity(123));
            var snapshot = new VoyageSnapshot { Origin = "Колосс", Bank = 50000, Clock = 100,
                Orders = [new("real-order", "helios", "Заказ", "FoodBurgerPlain", 3, 4500, "Колосс", "Бургеры ×3")],
                Crews = [new(Guid.NewGuid(), "cargo", 1)] };
            live.Apply(snapshot); live.SelectSystem("helios");
            var card = Descendants(live).OfType<Button>().Single(b => b.Text == "В корзину доставки");
            snapshot.Clock += 2; live.Apply(snapshot);
            Assert.That(Descendants(live).OfType<Button>().Single(b => b.Text == "В корзину доставки"), Is.SameAs(card), "A clock-only refresh must not interrupt a click by replacing the button.");
            live.Dispose();
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SeparateCartsOnlyConfirmTheMatchingQuoteAndClearTheCommittedBasket()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var requests = new List<VoyageRequest>();
            using var window = new VoyagePrototypeWindow(new NetEntity(123));
            window.Request += requests.Add;
            window.OpenCentered();
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var snapshot = new VoyageSnapshot { Origin = "Колосс", Bank = 50000,
                Known = ["helios", "nereid"],
                Orders = [new("food", "helios", "Бургеры", "FoodBurgerPlain", 3, 4500, "Колосс", "Бургеры ×3")],
                Products = [new("steel", "SheetSteel", "Сталь", 150)] };
            window.Apply(snapshot); window.SelectSystem("helios");
            void Press(Button button) => ((Action<BaseButton.ButtonEventArgs>)typeof(BaseButton)
                .GetField("OnPressed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(button)!).Invoke(null!);
            Button Find(string text) => Descendants(window).OfType<Button>().Single(b => b.Text == text);
            Press(Find("В корзину доставки"));
            Press(Find("В корзину закупок"));
            window.SelectSystem("nereid"); Press(Find("В корзину закупок"));
            var calculate = Descendants(window).OfType<Button>().Where(b => b.Text == "Рассчитать корзину").ToArray();
            requests.Clear(); Press(calculate[0]);
            Assert.That(requests.Single().Action, Is.EqualTo(VoyageAction.QuoteImportCart));
            Assert.That(requests[0].Cart.Select(l => l.Sector), Is.EquivalentTo(new[] { "helios", "nereid" }));
            Assert.That(requests[0].Cart.All(l => l.Product == "steel" && l.Order == ""), Is.True);
            var operation = requests[0].Operation;
            VoyageCartQuoteView Quote(Guid id) => new(id, true, 2, 6000, 300, 0, 8000, 1700,
                [new("helios", "Гелиос", "Сталь", "Транспорт", 1, 60, .1, 300, 0, 8000)]);
            snapshot.CartQuote = Quote(Guid.NewGuid()); window.Apply(snapshot);
            Assert.That(Descendants(ui.WindowRoot).OfType<Button>().Any(b => b.Text == "Нанять экипажи и отправить корзину"), Is.False);
            snapshot.CartQuote = Quote(operation); window.Apply(snapshot);
            var confirm = Descendants(ui.WindowRoot).OfType<Button>().Single(b => b.Text == "Нанять экипажи и отправить корзину");
            Assert.That(requests, Has.Count.EqualTo(1), "Receiving a quote must not spend money.");
            Press(confirm);
            Assert.That(requests.Last().Action, Is.EqualTo(VoyageAction.ConfirmCart));
            Assert.That(requests.Last().Quote, Is.EqualTo(operation));
            snapshot.CartCommitted = operation; window.Apply(snapshot);
            requests.Clear(); Press(calculate[0]);
            Assert.That(requests, Is.Empty, "Only the committed import basket is cleared.");
            Press(calculate[1]);
            Assert.That(requests.Single().Action, Is.EqualTo(VoyageAction.QuoteDeliveryCart));
            Assert.That(requests[0].Cart.Single().Order, Is.EqualTo("food"));
            window.Close();
        });
        await pair.CleanReturnAsync();
    }
}

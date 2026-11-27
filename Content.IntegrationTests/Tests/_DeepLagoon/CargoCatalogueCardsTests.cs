using Robust.Shared.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Client.Cargo.UI;
using Content.Shared._NF.Bank;
using Content.Shared.Cargo.Components;
using Content.Shared.Cargo.Prototypes;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;
[TestFixture, NonParallelizable]
public sealed class CargoCatalogueCardsTests
{
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (var child in root.Children) { yield return child; foreach (var next in Descendants(child)) yield return next; }
    }
    [Test]
    public async Task CategoryCardsUseRealSpritesAndCompactProductsKeepSelectionAndCurrentPricesAcrossUpdates()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        CargoConsoleMenu menu = null!; EntityUid console = default;
        Button category = null!, back = null!; LineEdit search = null!;
        CargoProductRow chosen = null!;
        CargoProductPrototype selected = null!;
        void Layout(float width)
        {
            var size = new Vector2(width, 720); menu.SetSize = size;
            menu.Measure(size); menu.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
            foreach (var card in Descendants(menu).Where(c => c.VisibleInTree && (c.Name == "CargoCategory" || c is CargoProductRow)))
            {
                Assert.That(card.GlobalPosition.X, Is.GreaterThanOrEqualTo(menu.GlobalPosition.X));
                Assert.That(card.GlobalPosition.X + card.Width, Is.LessThanOrEqualTo(menu.GlobalPosition.X + menu.Width + 1));
            }
        }
        await pair.Client.WaitAssertion(() =>
        {
            var e = pair.Client.EntMan; var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            console = e.SpawnEntity("ComputerCargoOrders", MapCoordinates.Nullspace);
            menu = new CargoConsoleMenu(console, e, prototypes, e.System<SpriteSystem>()); menu.OpenCentered();
            menu.PopulateProducts(); menu.PopulateOrders([]);
            var cards = Descendants(menu).OfType<Button>().Where(b => b.Name == "CargoCategory").ToArray();
            Assert.That(cards.Length, Is.GreaterThan(1));
            Assert.That(Descendants(menu).OfType<CargoProductRow>(), Is.Empty, "The first screen must be categories, not the entire product list.");
            foreach (var card in cards)
            {
                var icons = Descendants(card).OfType<TextureRect>().ToArray();
                Assert.That(icons.Length, Is.InRange(1, 3)); Assert.That(icons.All(i => i.Texture != null), Is.True);
            }
            Layout(740); Layout(1040);
            var grid = Descendants(menu).OfType<CargoCatalogueGrid>().Single(g => g.Name == "CategoryCards");
            Assert.That(grid.Columns, Is.EqualTo(3));
            var groups = menu.ProductPrototypes.GroupBy(p => p.Category).OrderBy(g => Loc.GetString(g.Key), StringComparer.CurrentCultureIgnoreCase).ToArray();
            category = cards[Array.FindIndex(groups, g => g.Count() >= 3)];
            back = Descendants(menu).OfType<Button>().Single(b => b.Name == "BackToCategories");
            search = Descendants(menu).OfType<LineEdit>().Single();
            menu.OnItemSelected += args => selected = ((CargoProductRow)args.Button.Parent!).Product!;
        });
        async Task Click(Button button)
        {
            var location = new ScreenCoordinates(button.GlobalPixelPosition + button.PixelSize / 2, button.Window!.Id);
            foreach (var state in new[] { BoundKeyState.Down, BoundKeyState.Up })
                await pair.Client.DoGuiEvent(button, new GUIBoundKeyEventArgs(EngineKeyFunctions.UIClick, state, location, false, Vector2.Zero, Vector2.Zero));
        }
        await Click(category);
        await pair.Client.WaitAssertion(() =>
        {
            Layout(740); Layout(1040);
            var products = Descendants(menu).OfType<CargoProductRow>().Where(c => c.VisibleInTree).ToArray();
            Assert.That(products.Length, Is.GreaterThanOrEqualTo(3));
            Assert.That(products.Select(p => p.Product!.Category).Distinct().Count(), Is.EqualTo(1));
            Assert.That(products.All(p => p.Height <= 115 && p.Icon.Texture != null), Is.True);
            chosen = products[0];
            var price = chosen.Product!.Cost + 137;
            menu.Prices[chosen.Product.ID] = price; menu.PopulateProducts(); menu.PopulateCategories();
            Assert.That(Descendants(menu).OfType<CargoProductRow>().First(), Is.SameAs(chosen), "A bank/price refresh must not replace a button during a click.");
            Assert.That(chosen.PointCost.Text, Is.EqualTo(BankSystemExtensions.ToSpesoString(price)));
            var minimum = menu.Width;
            for (var i = 0; i < 5; i++) { Layout(1040); Assert.That(menu.Width, Is.EqualTo(minimum)); }
        });
        await Click(chosen.MainButton);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(selected.ID, Is.EqualTo(chosen.Product!.ID), "Keep the existing order-dialog event and its product identity.");
            search.SetText("qqqz12345", true);
            Assert.That(Descendants(menu).OfType<CargoProductRow>(), Is.Empty);
        });
        await Click(back);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(search.Text, Is.Empty);
            Assert.That(Descendants(menu).Single(c => c.Name == "CategoryCards").Visible, Is.True);
            search.SetText(selected.Name, true);
            Assert.That(Descendants(menu).OfType<CargoProductRow>().Any(p => p.Product!.ID == selected.ID), Is.True);
            Assert.That(Descendants(menu).OfType<TabContainer>().Single().Children.Count, Is.EqualTo(2));
            menu.Close(); menu.Dispose(); pair.Client.EntMan.DeleteEntity(console);
        });
        await pair.CleanReturnAsync();
    }
}

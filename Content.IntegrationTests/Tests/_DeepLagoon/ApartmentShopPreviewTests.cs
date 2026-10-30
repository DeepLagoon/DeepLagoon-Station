using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text.Json;
using Content.Client._DeepLagoon.Apartments;
using Content.Client._DeepLagoon.WebUI;
using Robust.Client.UserInterface;
using Content.Server._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Apartments;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Player;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentShopPreviewTests
{
    [Test]
    public async Task PreviewContainsMapAndFurnitureWithoutChangingOwnershipLayoutOrLeavingGrids()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        ApartmentRoomScene captured = null!;
        List<ApartmentShopEntry> items = null!;
        var token = Guid.NewGuid();
        await pair.Server.WaitAssertion(() =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var entities = pair.Server.EntMan;
            var system = entities.System<ApartmentSystem>();
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            var user = pair.Server.PlayerMan.Sessions.Single();
            var template = pair.Server.ResolveDependency<IPrototypeManager>().Index<ApartmentTemplatePrototype>("DLApartmentBasic");
            var store = (IApartmentLayoutStore) typeof(ApartmentSystem).GetProperty("Store", flags)!.GetValue(system)!;
            var before = store.Load(user.UserId.UserId);
            var owned = store.Owned(user.UserId.UserId, template.ID);
            var count = entities.EntityQuery<MapGridComponent>().Count();
            var preview = (ApartmentRoomScene) typeof(ApartmentSystem).GetMethod("BuildShopPreview", flags)!.Invoke(system, new object[] { user.UserId, template })!;
            captured = preview;
            items = (List<ApartmentShopEntry>) typeof(ApartmentSystem).GetMethod("ShopItems", flags)!.Invoke(system, Array.Empty<object>())!;
            Assert.Multiple(() =>
            {
                Assert.That(preview.Width, Is.EqualTo(template.Width));
                Assert.That(preview.Height, Is.EqualTo(template.Height));
                Assert.That(preview.Tiles, Is.Not.Empty);
                Assert.That(preview.Objects.Any(item => item.Prototype == "PottedPlant2"), Is.True, "Preview includes the map furniture omitted by CaptureRoom");
                Assert.That(entities.EntityQuery<MapGridComponent>().Count(), Is.EqualTo(count), "Temporary preview grids must be deleted");
                Assert.That(system.Instances, Is.Empty);
                Assert.That(store.Load(user.UserId.UserId)?.Template, Is.EqualTo(before?.Template));
                Assert.That(store.Owned(user.UserId.UserId, template.ID), Is.EqualTo(owned));
            });
            // The public action path must reject a stale window token before loading a map.
            typeof(ApartmentSystem).GetMethod("ShopAction", flags)!.Invoke(system, new object[] {
                new ApartmentShopAction(Guid.NewGuid(), "preview", template.ID), new EntitySessionEventArgs(user) });
            var requests = (Dictionary<NetUserId, TimeSpan>) typeof(ApartmentSystem).GetField("_lastShopPreview", flags)!.GetValue(system)!;
            Assert.That(requests.ContainsKey(user.UserId), Is.False);
        });
        await pair.Client.WaitAssertion(() =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var shop = pair.Client.EntMan.System<ApartmentShopSystem>();
            typeof(ApartmentShopSystem).GetMethod("OnState", flags)!.Invoke(shop, new object[] {
                new ApartmentShopState(token, new() { new ApartmentShopEntry { Id = "DLApartmentBasic", Name = "Базовая квартира", Category = "Классика", Width = captured.Width, Height = captured.Height, Owned = true, Selected = true } }, 12345678, true, false, "", furniture: items, balance: 200) });
            typeof(ApartmentShopSystem).GetMethod("OnPreview", flags)!.Invoke(shop, new object[] {
                new ApartmentShopPreview(token, "DLApartmentBasic", captured) });
            var payload = (TguiData) typeof(ApartmentShopSystem).GetField("_preview", flags)!.GetValue(shop)!;
            using var json = JsonDocument.Parse(payload.ToString());
            var tiles = json.RootElement.GetProperty("tiles").EnumerateArray();
            Assert.That(tiles.Any(tile => tile.GetProperty("images").EnumerateArray().Any(image =>
                image.GetProperty("url").GetString()!.StartsWith("data:image/png;base64,"))), Is.True);
            Assert.That(json.RootElement.GetProperty("objects").EnumerateArray().Any(item => item.GetProperty("images").GetArrayLength() > 0), Is.True);
            var walls = json.RootElement.GetProperty("objects").EnumerateArray()
                .Where(item => item.GetProperty("prototype").GetString() == "WallPlastitaniumIndestructible").ToArray();
            Assert.That(walls, Is.Not.Empty, "The apartment's enclosing walls must reach the browser");
            foreach (var wall in walls)
            {
                Assert.That(wall.GetProperty("depth").GetInt32(), Is.LessThan(0), "Exercise negative engine draw depths");
                Assert.That(wall.GetProperty("images").EnumerateArray().Any(image =>
                    image.GetProperty("url").GetString()!.StartsWith("data:image/png;base64,")), Is.True);
            }
            var window = pair.Client.ResolveDependency<IUserInterfaceManager>().WindowRoot.Children.OfType<ApartmentTravelWindow>().Single();
            Assert.That(typeof(TguiPanel).GetField("_forceTgui", flags)!.GetValue(window.Panel), Is.True);
            Assert.That(typeof(TguiPanel).GetField("_showWindowControls", flags)!.GetValue(window.Panel), Is.False);
            var state = (string) typeof(TguiPanel).GetField("_data", flags)!.GetValue(window.Panel)!;
            using var shopJson = JsonDocument.Parse(state);
            var products = shopJson.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.That(products, Is.Not.Empty);
            foreach (var product in products)
                Assert.That(product.GetProperty("images").EnumerateArray().Any(image =>
                    image.GetProperty("url").GetString()!.StartsWith("data:image/png;base64,")), Is.True,
                    $"Missing catalogue image for {product.GetProperty("id").GetString()}");
            // An optional artifact feeds the browser regression with the real map and product sprites.
            if (Environment.GetEnvironmentVariable("APARTMENT_SHOP_BROWSER_FIXTURE") is { Length: > 0 } fixture)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fixture)!);
                File.WriteAllText(fixture, state);
                TestContext.AddTestAttachment(fixture);
            }
            window.Close();
        });
        await pair.CleanReturnAsync();
    }
}

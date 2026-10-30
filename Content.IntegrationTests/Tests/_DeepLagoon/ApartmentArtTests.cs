using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Content.Server._DeepLagoon.Apartments;
using Content.Server._DeepLagoon.Art;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Art;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Robust.Shared.Asynchronous;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentArtTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task PhysicalPurchasesChargeOnceDeliverOnceAndRejectChangedQuotes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        var db = pair.Server.ResolveDependency<IServerDbManager>();
        var tasks = pair.Server.ResolveDependency<ITaskManager>();
        ICommonSession session = null;
        ApartmentSystem apartments = null;
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            session = pair.Server.PlayerMan.Sessions.Single();
            apartments = pair.Server.EntMan.System<ApartmentSystem>();
            tasks.BlockWaitOnTask(pair.Server.EntMan.System<Content.Server._DeepLagoon.Currency.LagoonCoinSystem>()
                .Grant(session.UserId, 200, "apartment-item-test", null));
        });
        var frameOrder = Guid.NewGuid();
        await Buy("DLApartmentFrame", 35, frameOrder);
        Assert.That(await db.GetLagoonCoinsAsync(session.UserId), Is.EqualTo(165));
        await Buy("DLApartmentFrame", 35, frameOrder);
        Assert.That(await db.GetLagoonCoinsAsync(session.UserId), Is.EqualTo(165));
        await Buy("DLApartmentCanvas", 100, frameOrder); // conflicting replay
        Assert.That(await db.GetLagoonCoinsAsync(session.UserId), Is.EqualTo(165));
        await Buy("DLApartmentCanvas", 1, Guid.NewGuid()); // client cannot select its price
        Assert.That(await db.GetLagoonCoinsAsync(session.UserId), Is.EqualTo(165));
        await Buy("DLApartmentCanvas", 100, Guid.NewGuid());
        await Buy("DLApartmentFigurineClown", 15, Guid.NewGuid());
        Assert.That(await db.GetLagoonCoinsAsync(session.UserId), Is.EqualTo(50));
        await Buy("DLApartmentCanvas", 100, Guid.NewGuid()); // insufficient funds, no delivery
        Assert.That(await db.GetLagoonCoinsAsync(session.UserId), Is.EqualTo(50));
        var interrupted = Guid.NewGuid();
        await pair.Server.WaitPost(() =>
        {
            var store = (IApartmentLayoutStore) typeof(ApartmentSystem).GetProperty("Store", Private)!.GetValue(apartments)!;
            store.BeginItemOrder(session.UserId.UserId, interrupted, "DLApartmentFrame", 35);
            tasks.BlockWaitOnTask(db.PurchaseEconomyAsync(session.UserId, new LagoonEconomyPurchase(
                "apartment-item:" + session.UserId.UserId.ToString("N") + ":" + interrupted.ToString("N"),
                "apartment-item", "DLApartmentFrame", 35, 0)));
            // Simulate a process ending after payment, before delivery.
            typeof(ApartmentSystem).GetMethod("Cleanup", Private)!.Invoke(apartments, null);
        });
        Assert.That(await db.GetLagoonCoinsAsync(session.UserId), Is.EqualTo(15));
        await Buy("", -1, interrupted, "retryItem");
        Assert.That(await db.GetLagoonCoinsAsync(session.UserId), Is.EqualTo(15));
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var store = (IApartmentLayoutStore) typeof(ApartmentSystem).GetProperty("Store", Private)!.GetValue(apartments)!;
            Assert.That(store.PendingItemOrder(session.UserId.UserId), Is.Null);
            var home = apartments.Instances[session.UserId];
            AssertGoods(home.Grid);
            Assert.That(apartments.SaveApartment(home), Is.True);
            typeof(ApartmentSystem).GetMethod("Cleanup", Private)!.Invoke(apartments, null);
            home = apartments.PrepareElevatorApartment(session.UserId);
            AssertGoods(home.Grid);

            void AssertGoods(EntityUid grid)
            {
                var items = entities.EntityQuery<MetaDataComponent, TransformComponent>(true)
                    .Where(item => item.Item2.GridUid == grid).Select(item => item.Item1.EntityPrototype?.ID).ToArray();
                Assert.That(items.Count(id => id == "DLApartmentPaintingFrameItem"), Is.EqualTo(2));
                Assert.That(items.Count(id => id == "DLArtDurableCanvas"), Is.EqualTo(1));
                Assert.That(items.Count(id => id == "ToyFigurineClown"), Is.EqualTo(1));
            }
        });
        await pair.CleanReturnAsync();

        async Task Buy(string product, long price, Guid order, string action = "buyItem")
        {
            await pair.Server.WaitPost(() =>
            {
                typeof(ApartmentSystem).GetMethod("PublishShop", Private)!.Invoke(apartments, new object[] { session, "" });
                var tokens = (Dictionary<NetUserId, Guid>) typeof(ApartmentSystem).GetField("_shopTokens", Private)!.GetValue(apartments)!;
                typeof(ApartmentSystem).GetMethod("ShopAction", Private)!.Invoke(apartments, new object[] {
                    new ApartmentShopAction(tokens[session.UserId], action, product, order, price), new EntitySessionEventArgs(session) });
            });
            var busy = true;
            for (var i = 0; i < 150 && busy; i++)
            {
                await pair.RunTicksSync(3);
                await pair.Server.WaitPost(() => busy = ((HashSet<NetUserId>) typeof(ApartmentSystem)
                    .GetField("_shopping", Private)!.GetValue(apartments)!).Contains(session.UserId));
            }
            Assert.That(busy, Is.False, "The item purchase must complete or retain a retryable order.");
        }
    }

    [Test]
    public async Task CatalogueGoodsAndTheirContentsSurviveApartmentReload()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            var entities = pair.Server.EntMan;
            var apartments = entities.System<ApartmentSystem>();
            var owner = pair.Server.PlayerMan.Sessions.Single().UserId;
            var home = apartments.PrepareElevatorApartment(owner);
            var products = pair.Server.ResolveDependency<IPrototypeManager>().EnumeratePrototypes<ApartmentItemPrototype>().ToArray();
            foreach (var product in products)
            {
                var item = entities.SpawnEntity(product.Entity,
                    new EntityCoordinates(home.Grid, new Vector2(home.Template.Arrival.X + .5f, home.Template.Arrival.Y + .5f)));
                Assert.That(entities.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID,
                    Is.EqualTo(product.Entity.Id), product.ID);
                if (product.Entity.Id == "DiceBag")
                    Assert.That(entities.System<SharedContainerSystem>().GetContainer(item,
                        Content.Shared.Storage.StorageComponent.ContainerId).Count, Is.EqualTo(7));
                if (product.Entity.Id == "Lamp")
                    Assert.That(entities.System<SharedContainerSystem>().GetContainer(item, "cell_slot").Count, Is.EqualTo(1));
                if (product.Entity.Id == "BookBase")
                    entities.GetComponent<Content.Shared.Paper.PaperComponent>(item).Content = "My apartment journal";
            }
            var expected = Contents(home.Grid);
            Assert.That(apartments.SaveApartment(home), Is.True);
            typeof(ApartmentSystem).GetMethod("Cleanup", Private)!.Invoke(apartments, null);
            home = apartments.PrepareElevatorApartment(owner);
            Assert.That(Contents(home.Grid), Is.EquivalentTo(expected), "Reload must preserve goods and filled containers without duplicating their contents.");
            Assert.That(entities.EntityQuery<Content.Shared.Paper.PaperComponent, TransformComponent>(true)
                .Any(item => item.Item2.GridUid == home.Grid && item.Item1.Content == "My apartment journal"), Is.True);

            Dictionary<string, int> Contents(EntityUid grid) => entities.EntityQuery<MetaDataComponent, TransformComponent>(true)
                .Where(item => item.Item2.GridUid == grid && item.Item1.EntityPrototype != null)
                .GroupBy(item => item.Item1.EntityPrototype!.ID).ToDictionary(group => group.Key, group => group.Count());
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task GalleryShowsOnlyCurrentApartmentAndPaginatesNestedPaintingsThroughClientActions()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        var baked = ArtDocument.Bake(JsonSerializer.Serialize(new { version = 1, title = "Картина", layers = new[] {
            new { name = "Слой", visible = true, opacity = 1, png = Content.Tests._DeepLagoon.ArtTestsFixturePng.Create(), mask = "" } } }));
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            var entities = pair.Server.EntMan;
            var apartments = entities.System<ApartmentSystem>();
            var session = pair.Server.PlayerMan.Sessions.Single();
            var home = apartments.PrepareElevatorApartment(session.UserId);
            var coordinates = new EntityCoordinates(home.Grid, new Vector2(4.5f, 8.5f));
            var bag = entities.SpawnEntity("ClothingBackpack", coordinates);
            for (var i = 0; i < 15; i++)
            {
                var canvas = entities.SpawnEntity("DLArtCanvas", coordinates);
                var data = entities.GetComponent<ArtCanvasComponent>(canvas);
                data.Document = baked.Json; data.SavedPreview = baked.Preview; data.Title = $"Картина {i:00}";
                if (i == 14)
                    Assert.That(entities.System<SharedContainerSystem>().Insert(canvas,
                        entities.System<SharedContainerSystem>().GetContainer(bag, Content.Shared.Storage.StorageComponent.ContainerId)), Is.True);
            }
            entities.SpawnEntity("DLArtCanvas", coordinates); // blank, should be omitted
            var foreign = apartments.PrepareElevatorApartment(new NetUserId(Guid.NewGuid()));
            var foreignCanvas = entities.SpawnEntity("DLArtCanvas", new EntityCoordinates(foreign.Grid, new Vector2(4.5f, 8.5f)));
            var foreignArt = entities.GetComponent<ArtCanvasComponent>(foreignCanvas);
            foreignArt.Document = baked.Json; foreignArt.SavedPreview = baked.Preview; foreignArt.Title = "Чужая картина";
            typeof(ApartmentSystem).GetMethod("PublishShop", Private)!.Invoke(apartments, new object[] { session, "" });
        });
        await pair.RunTicksSync(30);
        await Page(0, 12);
        await Task.Delay(550); // same request pacing as the UI
        await Page(1, 3);
        await pair.CleanReturnAsync();

        async Task Page(int page, int expected)
        {
            await pair.Client.WaitPost(() =>
            {
                var shop = pair.Client.EntMan.System<Content.Client._DeepLagoon.Apartments.ApartmentShopSystem>();
                typeof(Content.Client._DeepLagoon.Apartments.ApartmentShopSystem).GetMethod("Action", Private)!
                    .Invoke(shop, new object[] { "gallery", JsonSerializer.Serialize(new { page }) });
            });
            await pair.RunTicksSync(30);
            await pair.Client.WaitAssertion(() =>
            {
                var type = typeof(Content.Client._DeepLagoon.Apartments.ApartmentShopSystem);
                var shop = pair.Client.EntMan.System<Content.Client._DeepLagoon.Apartments.ApartmentShopSystem>();
                var state = type.GetField("_gallery", Private)!.GetValue(shop);
                Assert.That(state, Is.Not.Null);
                using var json = JsonDocument.Parse(state!.ToString()!);
                var data = json.RootElement;
                Assert.That(data.GetProperty("loaded").GetBoolean(), Is.True);
                Assert.That(data.GetProperty("error").GetString(), Is.Empty);
                Assert.That(data.GetProperty("total").GetInt32(), Is.EqualTo(15));
                Assert.That(data.GetProperty("page").GetInt32(), Is.EqualTo(page));
                Assert.That(data.GetProperty("paintings").GetArrayLength(), Is.EqualTo(expected));
                Assert.That(data.GetProperty("paintings").EnumerateArray().Any(p => p.GetProperty("title").GetString() == "Чужая картина"), Is.False);
            });
        }
    }

    [Test]
    public async Task PrivateFrameRejectsOrdinaryCanvasAndPreservesDraftAndLayersAcrossUnloadAndRound()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var outside = await pair.CreateTestMap();
        var db = pair.Server.ResolveDependency<IServerDbManager>();
        var baked = ArtDocument.Bake(JsonSerializer.Serialize(new { version = 1, title = "Личная картина", layers = new[] {
            new { name = "Мой слой", visible = true, opacity = .5, png = Content.Tests._DeepLagoon.ArtTestsFixturePng.Create(), mask = "" } } }));
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            var entities = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            var apartments = entities.System<ApartmentSystem>();
            var hands = entities.System<SharedHandsSystem>();
            var containers = entities.System<SharedContainerSystem>();
            var user = entities.SpawnEntity("MobHuman", outside.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, user);
            Assert.That(apartments.Enter(session, session.UserId, out var message), Is.True, message);
            var home = apartments.Instances[session.UserId];
            var coords = entities.GetComponent<TransformComponent>(user).Coordinates;
            var frameItem = entities.SpawnEntity("DLApartmentPaintingFrameItem", coords);
            Assert.That(hands.TryPickupAnyHand(user, frameItem, animate: false), Is.True);
            var foreignWall = entities.SpawnEntity("WallSolid", outside.GridCoords.Offset(Vector2.UnitX));
            var foreign = new AfterInteractEvent(user, frameItem, foreignWall, outside.GridCoords, true);
            entities.EventBus.RaiseLocalEvent(frameItem, foreign);
            Assert.That(hands.IsHolding(user, frameItem, out _), Is.True, "An apartment frame cannot be installed on another grid.");
            var wallCoords = coords.Offset(Vector2.UnitX);
            var wall = entities.SpawnEntity("WallSolid", wallCoords);
            var install = new AfterInteractEvent(user, frameItem, wall, wallCoords, true);
            entities.EventBus.RaiseLocalEvent(frameItem, install);
            Assert.That(install.Handled, Is.True);
            var frame = entities.EntityQuery<ArtFrameComponent, ApartmentPaintingFrameComponent>().Single().Item1.Owner;
            Assert.That(entities.GetComponent<ApartmentPaintingFrameComponent>(frame).Owner, Is.EqualTo(session.UserId.UserId));
            Assert.That(entities.GetComponent<TransformComponent>(frame).Anchored, Is.True);
            var ordinary = entities.SpawnEntity("DLArtCanvas", coords);
            Assert.That(hands.TryPickupAnyHand(user, ordinary, animate: false), Is.True);
            var rejected = new InteractUsingEvent(user, ordinary, frame, wallCoords);
            entities.EventBus.RaiseLocalEvent(frame, rejected);
            Assert.That(rejected.Handled, Is.True);
            var slot = containers.GetContainer(frame, "art-canvas");
            Assert.That(slot.Count, Is.Zero);
            Assert.That(hands.IsHolding(user, ordinary, out _), Is.True);
            Assert.That(hands.TryDrop(user, ordinary), Is.True);
            Assert.That(containers.Insert(ordinary, slot), Is.False, "The container must also reject a direct ordinary-canvas insertion.");
            var canvas = entities.SpawnEntity("DLArtDurableCanvas", coords);
            var data = entities.GetComponent<ArtCanvasComponent>(canvas);
            data.DraftDocument = baked.Json;
            data.DraftPreview = baked.Preview;
            data.RecoveryId = Guid.NewGuid();
            Assert.That(data.RevisionHash, Is.Empty, "A private painting does not require a library revision.");
            Assert.That(hands.TryPickupAnyHand(user, canvas, animate: false), Is.True);
            var insert = new InteractUsingEvent(user, canvas, frame, wallCoords);
            entities.EventBus.RaiseLocalEvent(frame, insert);
            Assert.That(insert.Handled, Is.True);
            Assert.That(slot.ContainedEntities.Single(), Is.EqualTo(canvas));
            Assert.That(entities.GetComponent<ArtDisplayComponent>(frame).Preview, Is.EqualTo(baked.Preview));
            Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
            home.IdleSince = TimeSpan.FromSeconds(-10000);
            typeof(ApartmentSystem).GetField("_nextUpdate", Private)!.SetValue(apartments, TimeSpan.Zero);
            apartments.Update(1);
            Assert.That(entities.EntityExists(home.Grid), Is.False);
            Assert.That(apartments.Enter(session, session.UserId, out message), Is.True, message);
            AssertPainting();
            Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
            typeof(ApartmentSystem).GetMethod("Cleanup", Private)!.Invoke(apartments, null);
            Assert.That(apartments.Enter(session, session.UserId, out message), Is.True, message);
            AssertPainting();
            Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            entities.DeleteEntity(user);

            void AssertPainting()
            {
                var grid = apartments.Instances[session.UserId].Grid;
                var restoredFrame = entities.EntityQuery<ArtFrameComponent, TransformComponent>()
                    .Single(item => item.Item2.GridUid == grid).Item1.Owner;
                var restoredCanvas = containers.GetContainer(restoredFrame, "art-canvas").ContainedEntities.Single();
                var art = entities.GetComponent<ArtCanvasComponent>(restoredCanvas);
                Assert.That(entities.HasComponent<ArtDurableCanvasComponent>(restoredCanvas), Is.True);
                Assert.That(art.Document, Is.EqualTo(ArtLimits.EmptyDocument));
                Assert.That(art.DraftDocument, Is.EqualTo(baked.Json));
                Assert.That(art.DraftPreview, Is.EqualTo(baked.Preview));
                Assert.That(entities.GetComponent<ArtDisplayComponent>(restoredFrame).Preview, Is.EqualTo(baked.Preview));
                Assert.That(entities.GetComponent<ApartmentPaintingFrameComponent>(restoredFrame).Owner, Is.EqualTo(session.UserId.UserId));
                var gallery = (List<ApartmentPainting>) typeof(ApartmentSystem).GetMethod("GalleryContents", Private)!
                    .Invoke(apartments, new object[] { apartments.Instances[session.UserId] })!;
                Assert.That(gallery.Count, Is.EqualTo(1), "Empty ordinary canvases must not appear in the gallery.");
                Assert.That(gallery.Single().Title, Is.EqualTo("Личная картина"));
                Assert.That(gallery.Single().Preview, Is.EqualTo(baked.Preview));
                Assert.That(gallery.Single().Framed, Is.True);
            }
        });
        Assert.That(await db.GetArtworkPoolAsync(), Is.Empty, "Private framing must not publish to the public library.");
        await pair.CleanReturnAsync();
    }
}

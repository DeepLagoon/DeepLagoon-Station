using System.Linq;
using System.Text.Json;
using Content.Client._DeepLagoon.VendingMachines;
using Content.Server.Cargo.Systems;
using Content.Server.Lathe;
using Content.Server.Lathe.Components;
using Content.Server.Stack;
using Content.Server.VendingMachines;
using Content.Shared._Mono.Economy;
using Content.Shared._Mono.Economy.Components;
using Content.Shared.Lathe;
using Content.Shared.Research.Prototypes;
using Content.Shared.VendingMachines;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class VendingDemandTests
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: DLDemandProduct
  name: demand test product
  components:
  - type: StaticPrice
    price: 500
- type: entity
  id: DLDemandOtherProduct
  name: other demand test product
  components:
  - type: StaticPrice
    price: 500
- type: vendingMachineInventory
  id: DLDemandInventory
  startingInventory:
    DLDemandProduct: 4294967295
    DLDemandOtherProduct: 4294967295
  emaggedInventory:
    DLDemandProduct: 1
- type: entity
  id: DLDemandVendor
  components:
  - type: VendingMachine
    pack: DLDemandInventory
    requiresCash: true
  - type: Appearance
  - type: CreditReceiver
  - type: ItemSlots
  - type: ContainerContainer
    containers:
      cash_slot: !type:ContainerSlot
";

    [Test]
    public async Task PaidPurchasesRaiseOnlyThisMachinesPricesAndChargeTheDisplayedAmount()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var vendor = e.SpawnEntity("DLDemandVendor", map.GridCoords);
            var neighbor = e.SpawnEntity("DLDemandVendor", map.GridCoords);
            var actor = e.SpawnEntity(null, map.GridCoords);
            var component = e.GetComponent<VendingMachineComponent>(vendor);
            var other = e.GetComponent<VendingMachineComponent>(neighbor);
            var vending = e.System<VendingMachineSystem>();
            var cash = e.System<StackSystem>().Spawn(100000, "Credit", map.GridCoords);
            var receiver = e.GetComponent<CreditReceiverComponent>(vendor);
            Assert.That(e.System<SharedContainerSystem>().Insert(cash, receiver.CashSlot.ContainerSlot!), Is.True);
            var credit = e.System<SharedCreditReceiverSystem>();
            Assert.That(credit.GetCashBalance(vendor), Is.EqualTo(100000));
            Assert.That(component.Inventory["DLDemandProduct"].Price, Is.EqualTo(500));
            var spent = 0;
            for (var i = 0; i < 10; i++)
            {
                var price = component.Inventory["DLDemandProduct"].Price!.Value;
                vending.AuthorizedVend(vendor, actor, InventoryType.Regular, "DLDemandProduct", component);
                spent += price;
                Assert.That(credit.GetCashBalance(vendor), Is.EqualTo(100000 - spent));
                Assert.That(component.Purchases["DLDemandProduct"], Is.EqualTo(i + 1));
                Assert.That(component.LastPurchasePrice, Is.EqualTo(price));
                // A second request during dispensing must neither raise demand nor replace the receipt.
                vending.AuthorizedVend(vendor, actor, InventoryType.Regular, "DLDemandOtherProduct", component);
                Assert.That(component.Purchases["DLDemandProduct"], Is.EqualTo(i + 1));
                Assert.That(component.TotalPurchases, Is.EqualTo(i + 1));
                Assert.That(component.LastPurchasePrice, Is.EqualTo(price));
                vending.Update(component.EjectDelay + 0.1f);
            }
            Assert.That(spent, Is.EqualTo(5450));
            Assert.That(component.Inventory["DLDemandProduct"].Price, Is.EqualTo(600));
            Assert.That(component.EmaggedInventory["DLDemandProduct"].Price, Is.EqualTo(600));
            Assert.That(component.Inventory["DLDemandOtherProduct"].Price, Is.EqualTo(525));
            Assert.That(other.Inventory["DLDemandProduct"].Price, Is.EqualTo(500));
            Assert.That(other.Purchases, Is.Empty);
            Assert.That(component.Inventory["DLDemandProduct"].Amount, Is.EqualTo(uint.MaxValue));
            vending.RestockInventoryFromPrototype(vendor, component);
            Assert.That(component.Inventory["DLDemandProduct"].Price, Is.EqualTo(600));
            // No cash and no bank account: rejected purchases leave demand unchanged.
            e.DeleteEntity(cash);
            vending.AuthorizedVend(vendor, actor, InventoryType.Regular, "DLDemandProduct", component);
            Assert.That(component.TotalPurchases, Is.EqualTo(10));
            Assert.That(component.Inventory["DLDemandProduct"].Price, Is.EqualTo(600));
            component.RequiresCash = false;
            vending.AuthorizedVend(vendor, actor, InventoryType.Regular, "DLDemandProduct", component);
            Assert.That(component.TotalPurchases, Is.EqualTo(10));
            Assert.That(vending.GetPurchasePrice(vendor,
                pair.Server.ResolveDependency<IPrototypeManager>().Index<EntityPrototype>("DLDemandProduct"), component), Is.Zero);
            e.DeleteEntity(map.MapUid);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CatalogueUsesServerPricesEvenWhenPrototypeEstimateDiffers()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var inventory = new[] { new VendingMachineInventoryEntry(InventoryType.Regular, "FoodSnackChips", 1) { Price = 600 } };
            var catalogue = new VendingMachineTguiData(pair.Client.EntMan);
            using var data = JsonDocument.Parse(catalogue.BuildStatic(inventory, 10f, true));
            Assert.That(data.RootElement.GetProperty("items")[0].GetProperty("price").GetInt32(), Is.EqualTo(600));
            using var free = JsonDocument.Parse(catalogue.BuildStatic(inventory, 10f, false));
            Assert.That(free.RootElement.GetProperty("items")[0].GetProperty("price").GetInt32(), Is.Zero);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ActualLatheOutputSellsForTwiceTheUnprintedProduct()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var lathe = e.SpawnEntity("Autolathe", map.GridCoords);
            var component = e.GetComponent<LatheComponent>(lathe);
            Assert.That(component.ProductValueModifier, Is.EqualTo(2));
            var recipe = new LatheRecipePrototype { Result = "DLDemandProduct" };
            component.CurrentRecipe = recipe;
            var producing = e.EnsureComponent<LatheProducingComponent>(lathe);
            var normal = e.SpawnEntity("DLDemandProduct", MapCoordinates.Nullspace);
            var pricing = e.System<PricingSystem>();
            var originalPrice = pricing.GetPrice(normal);
            e.System<LatheSystem>().FinishProducing(lathe, component, producing);
            var printed = e.EntityQuery<MetaDataComponent>().Single(meta =>
                meta.EntityPrototype?.ID == "DLDemandProduct" && meta.Owner != normal).Owner;
            Assert.That(pricing.GetPrice(printed), Is.EqualTo(originalPrice * 2));
            e.DeleteEntity(normal);
            e.DeleteEntity(map.MapUid);
        });
        await pair.CleanReturnAsync();
    }
}
#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Content.Client._DeepLagoon.VendingMachines;
using Content.Client.VendingMachines;
using Content.Shared.VendingMachines;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class VendingMachineTguiTests
{
    [Test]
    public async Task VendingCataloguesAndIconsDoNotSpawnGameplayEntities()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true, Destructive = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.ResolveDependency<IEntityManager>();
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var count = entities.EntityCount;
            var catalogue = new VendingMachineTguiData(entities);
            var itemIds = prototypes.EnumeratePrototypes<VendingMachineInventoryPrototype>()
                .SelectMany(pack => pack.StartingInventory.Keys.Concat(pack.ContrabandInventory?.Keys.AsEnumerable() ?? Enumerable.Empty<EntProtoId>())
                    .Concat(pack.EmaggedInventory?.Keys.AsEnumerable() ?? Enumerable.Empty<EntProtoId>()))
                .Distinct().Where(id => prototypes.HasIndex<EntityPrototype>(id)).ToArray();
            Assert.That(itemIds, Is.Not.Empty);
            var inventory = itemIds.Select(id => new VendingMachineInventoryEntry(InventoryType.Regular, id, 1)).ToList();
            using var metadata = JsonDocument.Parse(catalogue.BuildStatic(inventory, 1.25f, true));
            Assert.That(metadata.RootElement.GetProperty("items").GetArrayLength(), Is.EqualTo(itemIds.Length));
            // Read the complete vending catalogue, including multilayer clothing and packed RSI resources.
            using var icons = JsonDocument.Parse(catalogue.BuildIcons(itemIds.Select(id => id.Id)));
            var images = icons.RootElement.GetProperty("data").GetProperty("iconBatch").GetProperty("icons");
            Assert.That(images.GetArrayLength(), Is.EqualTo(itemIds.Length));
            Assert.That(images.EnumerateArray().Any(icon => icon.GetProperty("layers").EnumerateArray().Any(layer =>
                layer.GetProperty("url").GetString()!.StartsWith("data:image/png;base64,"))), Is.True);
            Assert.That(entities.EntityCount, Is.EqualTo(count));

            using var free = JsonDocument.Parse(catalogue.BuildStatic(inventory.Take(3), 4f, false));
            Assert.That(free.RootElement.GetProperty("items").EnumerateArray().All(item => item.GetProperty("price").GetInt32() == 0), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SamePrototypeInDifferentInventoriesKeepsItsIdentityAndUnlimitedStock()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.ResolveDependency<IEntityManager>();
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            const string id = "FoodSnackChips";
            var inventory = new List<VendingMachineInventoryEntry>
            {
                new(InventoryType.Regular, id, 0),
                new(InventoryType.Contraband, id, uint.MaxValue),
                new(InventoryType.Emagged, id, 2),
            };
            var catalogue = new VendingMachineTguiData(entities);
            using var metadata = JsonDocument.Parse(catalogue.BuildStatic(inventory, 1.5f, true));
            var items = metadata.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.That(items.Select(item => item.GetProperty("key").GetString()).Distinct().Count(), Is.EqualTo(3));
            Assert.That(items.Select(item => item.GetProperty("type").GetInt32()), Is.EquivalentTo(new[] { 0, 1, 2 }));
            Assert.That(items.Select(item => item.GetProperty("price").GetInt32()), Is.All.EqualTo(
                VendingMachinePricing.GetPrice(prototypes.Index<EntityPrototype>(id), 1.5f, prototypes, entities)));
            using var stock = JsonDocument.Parse(new Content.Client._DeepLagoon.WebUI.TguiData().Array("stock", VendingMachineTguiData.Stock(inventory)).ToString());
            var entries = stock.RootElement.GetProperty("stock").EnumerateArray().ToArray();
            Assert.That(entries[0].GetProperty("available").GetBoolean(), Is.False);
            Assert.That(entries[1].GetProperty("unlimited").GetBoolean(), Is.True);
            Assert.That(entries[1].GetProperty("amount").GetString(), Is.EqualTo("4294967295"));
            Assert.That(entries[2].GetProperty("amount").GetString(), Is.EqualTo("2"));
        });
        await pair.CleanReturnAsync();
    }
}

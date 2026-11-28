using System;
using System.Linq;
using Content.Server._DeepLagoon.CryptoFarm;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server._Goobstation.ItemMiner;
using Content.Server._Mono.Cargo;
using Content.Server.Cargo.Systems;
using Content.Server.Stack;
using Content.Shared._Goobstation.ItemMiner;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class CryptoFarmTests
{
    [Test]
    public async Task WalletOnlyFarmHasNoStorageAndAnUnboundFarmCannotProduceCoins()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var farm = entities.SpawnEntity("DatafarmCrypto", map.GridCoords);
            Assert.That(entities.HasComponent<StorageComponent>(farm), Is.False);
            Assert.That(entities.HasComponent<UserInterfaceComponent>(farm), Is.False);
            var miner = entities.GetComponent<ItemMinerComponent>(farm);
            miner.PendingPayouts = 1000;
            miner.NeedApcPower = false;
            miner.NeedsAnchored = false;
            miner.NextAt = pair.Server.ResolveDependency<IGameTiming>().CurTime;
            entities.System<ItemMinerSystem>().Update(1);
            Assert.That(miner.PendingPayouts, Is.Zero);
            Assert.That(entities.EntityQuery<DriftingPriceComponent>().Any(), Is.False);
            var check = new ItemMinerCheckEvent(miner.Proto);
            entities.EventBus.RaiseLocalEvent(farm, ref check);
            Assert.That(check.Cancelled, Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MergingDifferentCoinPricesAndSplittingPreservesTotalCurrencyValue()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var stacks = entities.System<StackSystem>();
            var pricing = entities.System<PricingSystem>();
            var donor = entities.SpawnEntity("BitcoinContribcoin", map.GridCoords);
            var recipient = entities.SpawnEntity("BitcoinContribcoin", map.GridCoords);
            stacks.SetCount(donor, 3);
            stacks.SetCount(recipient, 2);
            entities.GetComponent<DriftingPriceComponent>(donor).CurrentPrice = 100;
            entities.GetComponent<DriftingPriceComponent>(recipient).CurrentPrice = 200;
            var before = pricing.GetPrice(donor) + pricing.GetPrice(recipient);
            Assert.That(before, Is.EqualTo(700));
            Assert.That(stacks.TryAdd(donor, recipient, 2), Is.True);
            Assert.That(pricing.GetPrice(donor) + pricing.GetPrice(recipient), Is.EqualTo(before).Within(.0001));
            Assert.That(entities.GetComponent<DriftingPriceComponent>(recipient).CurrentPrice, Is.EqualTo(150));
            var split = stacks.Split(recipient, 1, map.GridCoords)!.Value;
            Assert.That(entities.GetComponent<DriftingPriceComponent>(split).CurrentPrice, Is.EqualTo(150));
            Assert.That(pricing.GetPrice(donor) + pricing.GetPrice(recipient) + pricing.GetPrice(split), Is.EqualTo(before).Within(.0001));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UnboundFarmProducesNothingAndLegacyCoinsSurviveLongTermParking()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var farm = entities.SpawnEntity("DatafarmCrypto", map.GridCoords);
            var parking = entities.System<ParkingSystem>();
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            var farms = entities.System<CryptoFarmSystem>();
            var miner = entities.GetComponent<ItemMinerComponent>(farm);
            miner.NeedApcPower = false;
            miner.NeedsAnchored = false;
            miner.SpawnChance = 1;
            miner.MinedSound = null;
            var mining = entities.System<ItemMinerSystem>();
            miner.NextAt = pair.Server.ResolveDependency<IGameTiming>().CurTime;
            mining.Update(0);
            Assert.That(farms.StoredCoins(farm), Is.Zero, "Mining no longer spawns physical output, including when the exchange is closed.");
            var legacy = entities.SpawnEntity("BitcoinContribcoin", map.GridCoords);
            entities.System<StackSystem>().SetCount(legacy, 15);
            // Simulate an old parked machine, including its obsolete serialized storage.
            var containers = entities.System<Robust.Shared.Containers.SharedContainerSystem>();
            var buffer = containers.EnsureContainer<Robust.Shared.Containers.Container>(farm, StorageComponent.ContainerId);
            entities.AddComponent<StorageComponent>(farm).Grid.Add(new Robust.Shared.Maths.Box2i(0, 0, 4, 2));
            entities.AddComponent<UserInterfaceComponent>(farm);
            Assert.That(containers.Insert(legacy, buffer), Is.True);
            var restored = parking.LoadSnapshot(parking.FilteredSnapshot(map.Grid));
            var loaded = entities.EntityQuery<CryptoFarmComponent, TransformComponent>(true)
                .Single(entry => entry.Item2.GridUid == restored).Item1.Owner;
            Assert.That(farms.StoredCoins(loaded), Is.EqualTo(15));
            farms.RetireLegacyStorage(loaded);
            farms.RetireLegacyStorage(loaded);
            Assert.That(entities.HasComponent<StorageComponent>(loaded), Is.False);
            Assert.That(entities.HasComponent<UserInterfaceComponent>(loaded), Is.False);
            Assert.That(containers.TryGetContainer(loaded, StorageComponent.ContainerId, out _), Is.False);
            int CoinsOn(EntityUid grid) => entities.EntityQuery<DriftingPriceComponent, TransformComponent>(true)
                .Where(entry => entry.Item2.GridUid == grid && !entities.IsQueuedForDeletion(entry.Item1.Owner))
                .Sum(entry => entities.GetComponent<StackComponent>(entry.Item1.Owner).Count);
            Assert.That(CoinsOn(restored), Is.EqualTo(15));
            var second = parking.LoadSnapshot(parking.FilteredSnapshot(restored));
            var loadedAgain = entities.EntityQuery<CryptoFarmComponent, TransformComponent>(true)
                .Single(entry => entry.Item2.GridUid == second).Item1.Owner;
            Assert.That(entities.HasComponent<StorageComponent>(loadedAgain), Is.False);
            Assert.That(CoinsOn(second), Is.EqualTo(15));
            entities.DeleteEntity(restored);
            entities.DeleteEntity(second);
        });
        await pair.CleanReturnAsync();
    }
}

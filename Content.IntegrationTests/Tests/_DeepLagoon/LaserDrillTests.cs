using System;
using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.Mining;
using Content.Server._Goobstation.ItemMiner;
using Content.Server.Power.Components;
using Content.Shared._DeepLagoon.Mining;
using Content.Shared._Goobstation.ItemMiner;
using Content.Shared.Materials;
using Content.Shared.Materials.OreSilo;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class LaserDrillTests
{
    [TestCase("LaserDrill")]
    [TestCase("StationLaserDrill")]
    public async Task SelectedResourceGoesOnlyToConnectedSilo(string prototype)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var timing = pair.Server.ResolveDependency<IGameTiming>();
            var system = entities.System<ItemMinerSystem>();
            var mats = entities.System<SharedMaterialStorageSystem>();
            var drill = entities.SpawnEntity(prototype, map.GridCoords);
            var silo = entities.SpawnEntity("MachineMaterialSilo", map.GridCoords);
            var other = entities.SpawnEntity("MachineMaterialSilo", map.GridCoords);
            var actor = entities.SpawnEntity(null, map.GridCoords);
            var config = entities.GetComponent<LaserDrillComponent>(drill);
            var miner = entities.GetComponent<ItemMinerComponent>(drill);
            var consumer = entities.GetComponent<PowerConsumerComponent>(drill);
            consumer.PowerBypass = true;
            entities.GetComponent<ApcPowerReceiverComponent>(silo).Powered = true;
            entities.GetComponent<ApcPowerReceiverComponent>(other).Powered = true;
            // Isolate storage behavior from the normal expedition-only restriction.
            entities.RemoveComponent<PlanetMinerComponent>(drill);
            miner.NeedsAnchored = false;
            miner.MiningSound = null;
            int StackCount()
            {
                var count = 0;
                var query = entities.AllEntityQueryEnumerator<StackComponent>();
                while (query.MoveNext(out _, out _))
                    count++;
                return count;
            }
            var initialStacks = StackCount();

            void Select(string material) => entities.EventBus.RaiseLocalEvent(drill,
                new LaserDrillSelectMessage(material) { Actor = actor, UiKey = LaserDrillUiKey.Key });
            void Toggle(EntityUid target) => entities.EventBus.RaiseLocalEvent(target,
                new ToggleOreSiloClientMessage(entities.GetNetEntity(drill)) { Actor = actor, UiKey = OreSiloUiKey.Key });
            bool CanMine()
            {
                var check = new ItemMinerCheckEvent(miner.Proto);
                entities.EventBus.RaiseLocalEvent(drill, ref check);
                return !check.Cancelled;
            }
            void Cycle()
            {
                miner.NextAt = timing.CurTime - TimeSpan.FromMilliseconds(1);
                system.Update(0);
            }

            Assert.That(config.Materials, Has.Count.EqualTo(12));
            Assert.That(CanMine(), Is.False);
            Toggle(silo);
            Assert.That(entities.GetComponent<OreSiloClientComponent>(drill).Silo, Is.EqualTo(silo));
            Assert.That(CanMine(), Is.False, "A connected drill still requires a selection.");
            Select("Steel");
            Assert.That(config.SelectedMaterial, Is.Null, "Reject resources outside the configured ore list.");

            foreach (var (material, amount) in config.Materials)
            {
                mats.TryChangeMaterialAmount(silo, mats.GetStoredMaterials(silo, true)
                    .ToDictionary(p => p.Key, p => -p.Value), localOnly: true);
                Select(material);
                Assert.That(CanMine(), Is.True);
                Cycle();
                var stored = mats.GetStoredMaterials(silo, true);
                Assert.That(stored, Has.Count.EqualTo(1));
                Assert.That(stored[material], Is.EqualTo(amount));
                Assert.That(mats.GetStoredMaterials(drill, true), Is.Empty);
            }

            Select("RawIron");
            miner.PendingPayouts = 3;
            Select("RawGold");
            Assert.That(miner.PendingPayouts, Is.Zero);
            Assert.That(miner.NextAt, Is.EqualTo(timing.CurTime + miner.Interval));
            Toggle(other);
            Assert.That(entities.GetComponent<OreSiloClientComponent>(drill).Silo, Is.EqualTo(silo), "Cannot steal a client from another silo.");
            Assert.That(entities.GetComponent<OreSiloComponent>(other).Clients, Is.Empty);

            var before = mats.GetStoredMaterials(silo, true);
            void AssertStopped()
            {
                Assert.That(CanMine(), Is.False);
                Cycle();
                Assert.That(mats.GetStoredMaterials(silo, true), Is.EquivalentTo(before));
                Assert.That(miner.PendingPayouts, Is.Zero);
            }
            Toggle(silo);
            AssertStopped();
            Toggle(silo);
            entities.GetComponent<ApcPowerReceiverComponent>(silo).Powered = false;
            AssertStopped();
            entities.GetComponent<ApcPowerReceiverComponent>(silo).Powered = true;
            consumer.PowerBypass = false;
            AssertStopped();
            consumer.PowerBypass = true;
            var storage = entities.GetComponent<MaterialStorageComponent>(silo);
#pragma warning disable RA0002 // Test fixture: simulate a storage that cannot accept another payout.
            storage.StorageLimit = mats.GetTotalMaterialAmount(silo);
            AssertStopped();
            storage.StorageLimit = null;
#pragma warning restore RA0002
            var transforms = entities.System<SharedTransformSystem>();
            transforms.SetCoordinates(drill, new EntityCoordinates(map.Grid, new Vector2(500, 0)));
            AssertStopped();
            transforms.SetCoordinates(drill, map.GridCoords);
            Assert.That(CanMine(), Is.True);
            Assert.That(StackCount(), Is.EqualTo(initialStacks), "Mining must not create ore stacks on the floor.");
            var oreQuery = entities.AllEntityQueryEnumerator<MetaDataComponent>();
            while (oreQuery.MoveNext(out _, out var metadata))
                Assert.That(metadata.EntityPrototype?.ID, Is.Not.EqualTo("SpawnLootOres15"));
            entities.DeleteEntity(silo);
            Assert.That(entities.GetComponent<OreSiloClientComponent>(drill).Silo, Is.Null);
            Assert.That(CanMine(), Is.False);
        });
        await pair.Client.WaitAssertion(() =>
        {
            using var window = new LaserDrillWindow();
            window.SetState(new LaserDrillState(new() { { "RawIron", 1500 }, { "RawGold", 1500 } },
                "RawGold", "Test silo", LaserDrillStatus.Mining));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OrdinaryItemMinerStillSpawnsItsItem()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var uid = entities.SpawnEntity(null, map.GridCoords);
            var miner = entities.AddComponent<ItemMinerComponent>(uid);
            miner.Proto = "SteelOre15";
            miner.NeedApcPower = false;
            miner.NeedsAnchored = false;
            miner.MiningSound = null;
            miner.NextAt = pair.Server.ResolveDependency<IGameTiming>().CurTime - TimeSpan.FromMilliseconds(1);
            entities.System<ItemMinerSystem>().Update(0);
            var found = false;
            var query = entities.AllEntityQueryEnumerator<MetaDataComponent, StackComponent>();
            while (query.MoveNext(out _, out var metadata, out var stack))
            {
                if (metadata.EntityPrototype?.ID != "SteelOre15")
                    continue;
                Assert.That(stack.Count, Is.EqualTo(15));
                found = true;
            }
            Assert.That(found, Is.True);
        });
        await pair.CleanReturnAsync();
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server._Goobstation.ItemMiner;
using Content.Server.Gravity;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared._DeepLagoon.Mining;
using Content.Shared._Goobstation.ItemMiner;
using Content.Shared.Gravity;
using Content.Shared.Materials;
using Content.Shared.Materials.OreSilo;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ParkingMachineryRestoreTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task LongTermSnapshotRestoresChargedGravityGeneratorWithoutTogglingIt(bool active)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var parking = entities.System<ParkingSystem>();
            var generator = entities.SpawnEntity("GravityGenerator", map.GridCoords);
            var charge = entities.GetComponent<PowerChargeComponent>(generator);
            charge.Charge = active ? charge.MaxCharge : 0;
            charge.SwitchedOn = active;
            charge.Active = active;
            charge.ChargeRate = 0;
            if (active)
            {
                var activated = new ChargedMachineActivatedEvent();
                entities.EventBus.RaiseLocalEvent(generator, ref activated);
            }
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            var snapshot = parking.FilteredSnapshot(map.Grid);
            entities.DeleteEntity(map.Grid);
            var restored = parking.LoadSnapshot(snapshot);
            var children = entities.GetComponent<TransformComponent>(restored).ChildEnumerator;
            EntityUid machine = default;
            while (children.MoveNext(out var entity))
                if (entities.HasComponent<GravityGeneratorComponent>(entity)) machine = entity;
            Assert.That(machine.IsValid(), Is.True);
            Assert.That(entities.GetComponent<PowerChargeComponent>(machine).Active, Is.EqualTo(active));
            Assert.That(entities.GetComponent<GravityGeneratorComponent>(machine).GravityActive, Is.EqualTo(active),
                "A post-init load must reconstruct runtime activation even without a new charging transition.");
            Assert.That(entities.GetComponent<GravityComponent>(restored).Enabled, Is.EqualTo(active));
            entities.DeleteEntity(restored);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(0)]
    [TestCase(2)]
    public async Task LongTermSnapshotKeepsRemainingMiningCycleWithoutOfflineProduction(int earnedPayouts)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        string snapshot = "";
        EntityUid destination = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            destination = entities.GetComponent<TransformComponent>(map.Grid).MapUid!.Value;
            var drill = entities.SpawnEntity("LaserDrill", map.GridCoords);
            var silo = entities.SpawnEntity("MachineMaterialSilo", map.GridCoords);
            // Exercise the parking timer path independently of a live expedition's power network.
            entities.RemoveComponent<PlanetMinerComponent>(drill);
            entities.RemoveComponent<PowerConsumerMinerComponent>(drill);
            var miner = entities.GetComponent<ItemMinerComponent>(drill);
            miner.NeedApcPower = false;
            miner.NeedsAnchored = false;
            miner.MiningSound = null;
            miner.PendingPayouts = earnedPayouts;
            miner.NextAt = pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromSeconds(5);
            entities.GetComponent<LaserDrillComponent>(drill).SelectedMaterial = "RawIron";
            entities.GetComponent<ApcPowerReceiverComponent>(silo).Powered = true;
            entities.EventBus.RaiseLocalEvent(silo, new ToggleOreSiloClientMessage(entities.GetNetEntity(drill))
                { Actor = drill, UiKey = OreSiloUiKey.Key });
            Assert.That(entities.GetComponent<OreSiloClientComponent>(drill).Silo, Is.EqualTo(silo));
            var parking = entities.System<ParkingSystem>();
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            snapshot = parking.FilteredSnapshot(map.Grid);
            entities.DeleteEntity(map.Grid);
        });

        // Advance the simulation well beyond the saved five-second deadline while
        // no drill entity exists. Loading must preserve remaining time, not catch up.
        await pair.RunSeconds(30);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var parking = entities.System<ParkingSystem>();
            var restored = parking.LoadSnapshot(snapshot);
            EntityUid drill = default, silo = default;
            var children = entities.GetComponent<TransformComponent>(restored).ChildEnumerator;
            while (children.MoveNext(out var entity))
            {
                if (entities.HasComponent<LaserDrillComponent>(entity)) drill = entity;
                if (entities.HasComponent<OreSiloComponent>(entity)) silo = entity;
            }
            Assert.That(drill.IsValid() && silo.IsValid(), Is.True);
            var timing = pair.Server.ResolveDependency<IGameTiming>();
            var miner = entities.GetComponent<ItemMinerComponent>(drill);
            Assert.That(miner.PendingPayouts, Is.EqualTo(earnedPayouts));
            Assert.That((miner.NextAt - timing.CurTime).TotalSeconds, Is.EqualTo(5).Within(.1));
            entities.System<SharedTransformSystem>().SetCoordinates(restored,
                new EntityCoordinates(destination, new Vector2(10, 0)));
            entities.GetComponent<ApcPowerReceiverComponent>(silo).Powered = true;
            var mining = entities.System<ItemMinerSystem>();
            var materials = entities.System<SharedMaterialStorageSystem>();
            for (var tick = 0; tick < 10; tick++) mining.Update(0);
            var stored = materials.GetStoredMaterials(silo, true);
            Assert.That(stored.GetValueOrDefault("RawIron"), Is.EqualTo(
                earnedPayouts * entities.GetComponent<LaserDrillComponent>(drill).Materials["RawIron"]));
            Assert.That(miner.PendingPayouts, Is.Zero, "Only production earned before parking may be paid out.");
            Assert.That(miner.NextAt, Is.GreaterThan(timing.CurTime));
            entities.DeleteEntity(restored);
        });
        await pair.CleanReturnAsync();
    }
}

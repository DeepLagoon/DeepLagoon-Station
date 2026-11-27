using System;
using System.Linq;
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
#pragma warning disable RA0002 // Seed a pre-existing serialized charged state, without a new charging transition after load.
            charge.Charge = active ? charge.MaxCharge : 0;
            charge.SwitchedOn = active;
            charge.Active = active;
            charge.ChargeRate = 0;
#pragma warning restore RA0002
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
        var map = await pair.CreateTestMap(); string snapshot = ""; EntityUid destination = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            destination = entities.GetComponent<TransformComponent>(map.Grid).MapUid!.Value;
            // Laser drills have been retired; exercise the shared producer timer with a supported machine.
            var printer = entities.SpawnEntity("TechDiskPrinter", map.GridCoords);
            entities.RemoveComponent<PowerConsumerMinerComponent>(printer);
            var miner = entities.GetComponent<ItemMinerComponent>(printer);
            miner.Proto = "SteelOre1"; miner.ResolveRandomSpawner = false;
            miner.NeedApcPower = false; miner.NeedsAnchored = false; miner.MiningSound = null; miner.MinedSound = null;
            miner.PendingPayouts = earnedPayouts;
            miner.NextAt = pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromSeconds(5);
            var parking = entities.System<ParkingSystem>();
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            snapshot = parking.FilteredSnapshot(map.Grid); entities.DeleteEntity(map.Grid);
        });
        await pair.RunSeconds(30);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan; var restored = entities.System<ParkingSystem>().LoadSnapshot(snapshot);
            var printer = entities.EntityQuery<ItemMinerComponent, TransformComponent>(true).Single(p => p.Item2.GridUid == restored).Item1;
            var timing = pair.Server.ResolveDependency<IGameTiming>();
            Assert.That(printer.PendingPayouts, Is.EqualTo(earnedPayouts));
            Assert.That((printer.NextAt - timing.CurTime).TotalSeconds, Is.EqualTo(5).Within(.1));
            entities.System<SharedTransformSystem>().SetCoordinates(restored, new EntityCoordinates(destination, new Vector2(10, 0)));
            var mining = entities.System<ItemMinerSystem>();
            for (var tick = 0; tick < 10; tick++) mining.Update(0);
            Assert.That(entities.EntityQuery<MetaDataComponent, TransformComponent>(true).Count(p => p.Item2.GridUid == restored && p.Item1.EntityPrototype?.ID == "SteelOre1"), Is.EqualTo(earnedPayouts));
            Assert.That(printer.PendingPayouts, Is.Zero, "Only production earned before parking may be paid out.");
            Assert.That(printer.NextAt, Is.GreaterThan(timing.CurTime)); entities.DeleteEntity(restored);
        });
        await pair.CleanReturnAsync();
    }
}

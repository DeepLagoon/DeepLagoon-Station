using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Economy;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server.Shuttles.Components;
using Content.Shared._Goobstation.ItemMiner;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class EquipmentMaintenanceTests
{
    [Test]
    public async Task LiveToggleStopsWearImmediatelyRestoresThrustAndScheduleAndPreservesParkedWear()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var config = server.ResolveDependency<IConfigurationManager>();
            var maintenance = entities.System<EquipmentMaintenanceSystem>();
            Assert.That(config.GetCVar(SectorEconomyCVars.MaintenanceEnabled), Is.False);
            // This switch is independent of the new procurement rollout.
            Assert.That(config.GetCVar(SectorEconomyCVars.Enabled), Is.False);
            var ship = entities.EnsureComponent<ParkingShuttleComponent>(map.Grid);
            ship.OwnerAccount = Guid.NewGuid();
            var engine = entities.SpawnEntity("Thruster", map.GridCoords);
            var thruster = entities.GetComponent<ThrusterComponent>(engine);
            var originalThrust = thruster.Thrust;
            maintenance.Work(engine, 10);
            Assert.That(entities.HasComponent<EquipmentMaintenanceComponent>(engine), Is.False);
            config.SetCVar(SectorEconomyCVars.MaintenanceEnabled, true);
            maintenance.Work(engine, 10);
            var wear = entities.GetComponent<EquipmentMaintenanceComponent>(engine);
            wear.WorkingSeconds = 28800;
            // A real miner's current cycle should also recover when disabled mid-round.
            var miner = entities.EnsureComponent<ItemMinerComponent>(engine);
            miner.Proto = "SheetSteel";
            var now = server.ResolveDependency<IGameTiming>().CurTime;
            miner.NextAt = now + TimeSpan.FromSeconds(100);
            wear.AppliedMinerFactor = 1;
            config.SetCVar(SectorEconomyCVars.MaintenanceEnabled, false);
            config.SetCVar(SectorEconomyCVars.MaintenanceEnabled, true);
            Assert.That(thruster.Thrust, Is.EqualTo(originalThrust * .8f).Within(.01f));
            Assert.That((miner.NextAt - now).TotalSeconds, Is.EqualTo(125).Within(.01));
            config.SetCVar(SectorEconomyCVars.MaintenanceEnabled, false);
            Assert.That(thruster.Thrust, Is.EqualTo(originalThrust).Within(.01f));
            Assert.That((miner.NextAt - now).TotalSeconds, Is.EqualTo(100).Within(.01));
            maintenance.Work(engine, 10);
            Assert.That(wear.WorkingSeconds, Is.EqualTo(28800));
            Assert.That(maintenance.Efficiency(engine), Is.EqualTo(1));
            config.SetCVar(SectorEconomyCVars.MaintenanceEnabled, true);
            ship.Parked = true;
            wear.WorkingSeconds = 15000;
            maintenance.Work(engine, 10);
            Assert.That(wear.WorkingSeconds, Is.EqualTo(15000), "Parking cannot accrue use or erase saved wear.");
            var snapshot = entities.System<ParkingSystem>().SerializeSnapshot(map.Grid);
            Assert.That(snapshot, Does.Contain("EquipmentMaintenance").And.Contain("workingSeconds: 15000"));
            var restored = entities.System<ParkingSystem>().LoadSnapshot(snapshot);
            var restoredEngine = entities.EntityQuery<EquipmentMaintenanceComponent, TransformComponent>(true)
                .Single(p => p.Item2.GridUid == restored).Item1.Owner;
            var restoredWear = entities.GetComponent<EquipmentMaintenanceComponent>(restoredEngine);
            Assert.That(restoredWear.WorkingSeconds, Is.EqualTo(15000));
            entities.GetComponent<ParkingShuttleComponent>(restored).Parked = false;
            config.SetCVar(SectorEconomyCVars.MaintenanceEnabled, false);
            config.SetCVar(SectorEconomyCVars.MaintenanceEnabled, true);
            Assert.That(entities.GetComponent<ThrusterComponent>(restoredEngine).Thrust,
                Is.EqualTo(originalThrust * EquipmentMaintenanceSystem.Factor(15000)).Within(.01f),
                "Part recalculation during restoration must not bypass maintenance.");
            config.SetCVar(SectorEconomyCVars.MaintenanceEnabled, false);
            entities.DeleteEntity(restored);
        });
        await pair.CleanReturnAsync();
    }
}

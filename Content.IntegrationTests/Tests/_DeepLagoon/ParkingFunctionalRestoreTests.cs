using System;
using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server.Ame.Components;
using Content.Server.Ame.EntitySystems;
using Content.Server.Lathe;
using Content.Server.Lathe.Components;
using Content.Server.Power.Components;
using Content.Server.Materials;
using Content.Shared.Ame.Components;
using Content.Shared._Crescent.Hardpoints;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Lathe;
using Content.Shared.Materials;
using Content.Shared.Projectiles;
using Content.Shared.Research.Prototypes;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ParkingFunctionalRestoreTests
{
    [Test]
    public async Task LoadedGunCanActuallyFireAcrossTwoSnapshots()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        EntityUid grid = map.Grid.Owner, gunId = default, destination = default;
        void FireAndAssert(string stage)
        {
            var entities = pair.Server.EntMan;
            var gun = entities.GetComponent<GunComponent>(gunId);
            Assert.That(entities.HasComponent<Content.Shared._Mono.ShipGuns.ShipGunNoRecoilComponent>(gunId), Is.True, stage);
            var hull = entities.GetComponent<PhysicsComponent>(grid);
            entities.System<SharedPhysicsSystem>().SetBodyType(grid, BodyType.Dynamic, body: hull);
            Assert.That(hull.Mass, Is.Positive, "A static or massless hull cannot prove absence of recoil.");
            var linearBefore = hull.LinearVelocity;
            var angularBefore = hull.AngularVelocity;
            var ammoBefore = new GetAmmoCountEvent();
            entities.EventBus.RaiseLocalEvent(gunId, ref ammoBefore);
            Assert.That(ammoBefore.Count, Is.Positive, stage);
            var projectilesBefore = entities.Count<ProjectileComponent>();
            entities.System<SharedGunSystem>().AttemptShoot(gunId, gun);
            var ammoAfter = new GetAmmoCountEvent();
            entities.EventBus.RaiseLocalEvent(gunId, ref ammoAfter);
            Assert.That(ammoAfter.Count, Is.EqualTo(ammoBefore.Count - 1), $"{stage}: consume one actual cartridge.");
            Assert.That(entities.Count<ProjectileComponent>(), Is.GreaterThan(projectilesBefore),
                $"{stage}: create an actual projectile.");
            Assert.That(entities.GetComponent<TransformComponent>(gunId).Anchored, Is.True,
                $"{stage}: firing must not eject a mounted gun into space.");
            Assert.That(entities.GetComponent<TransformComponent>(gunId).GridUid, Is.EqualTo(grid));
            Assert.That(hull.LinearVelocity, Is.EqualTo(linearBefore), $"{stage}: no linear recoil on the hull.");
            Assert.That(hull.AngularVelocity, Is.EqualTo(angularBefore), $"{stage}: no angular recoil on the hull.");
        }
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var parking = entities.System<ParkingSystem>();
            destination = entities.GetComponent<TransformComponent>(grid).MapUid!.Value;
            var mountCoordinates = new EntityCoordinates(grid, new Vector2(.5f, .5f));
            var mount = entities.SpawnEntity("HardpointBallisticLight", mountCoordinates);
            gunId = entities.SpawnEntity("WeaponTurretL85Autocannon", mountCoordinates);
            Assert.That(entities.GetComponent<TransformComponent>(gunId).Anchored, Is.True);
            Assert.That(entities.GetComponent<HardpointComponent>(mount).anchoring, Is.EqualTo(gunId));
            // The default solid loader is infinite. Load the existing finite AS magazine so the
            // regression verifies consumption as well as projectile creation.
            var slots = entities.System<ItemSlotsSystem>();
            Assert.That(slots.TryEject(gunId, "gun_magazine", null, out var originalLoader), Is.True);
            entities.DeleteEntity(originalLoader!.Value);
            var finiteLoader = entities.SpawnEntity("Magazine20mmAS", mountCoordinates);
            Assert.That(slots.TryInsert(gunId, "gun_magazine", finiteLoader, null), Is.True);
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            FireAndAssert("Before parking");
            var handheld = entities.SpawnEntity("WeaponPistolViper", new EntityCoordinates(destination, new Vector2(20, 20)));
            var handheldGun = entities.GetComponent<GunComponent>(handheld);
            var handheldBody = entities.GetComponent<PhysicsComponent>(handheld);
            Assert.That(entities.HasComponent<Content.Shared._Mono.ShipGuns.ShipGunNoRecoilComponent>(handheld), Is.False);
            entities.System<SharedPhysicsSystem>().SetBodyType(handheld, BodyType.Dynamic, body: handheldBody);
            var before = handheldBody.LinearVelocity;
            entities.System<SharedGunSystem>().CauseImpulse(new EntityCoordinates(destination, new Vector2(30, 20)), (handheld, handheldGun), 1);
            Assert.That(handheldBody.LinearVelocity, Is.Not.EqualTo(before), "Handheld weapons retain their physical recoil.");
            entities.DeleteEntity(handheld);
        });
        for (var cycle = 0; cycle < 2; cycle++)
        {
            // Let cooldowns expire normally before firing the restored mounted weapon.
            await pair.RunSeconds(2);
            await pair.Server.WaitAssertion(() =>
            {
                var entities = pair.Server.EntMan;
                var parking = entities.System<ParkingSystem>();
                Assert.That(entities.GetComponent<TransformComponent>(gunId).GridUid, Is.EqualTo(grid),
                    $"Cycle {cycle}: the firing fixture must still belong to the ship being saved.");
                var snapshot = parking.FilteredSnapshot(grid);
                entities.DeleteEntity(grid);
                grid = parking.LoadSnapshot(snapshot);
                gunId = entities.EntityQuery<GunComponent, TransformComponent>(true)
                    .Single(entry => entry.Item2.GridUid == grid).Item1.Owner;
                entities.System<SharedTransformSystem>().SetCoordinates(grid,
                    new EntityCoordinates(destination, new Vector2(10 * (cycle + 1), 0)));
            });
            await pair.RunSeconds(2);
            await pair.Server.WaitAssertion(() => FireAndAssert($"Restore cycle {cycle}"));
        }
        await pair.Server.WaitAssertion(() => pair.Server.EntMan.DeleteEntity(grid));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PaidLatheRecipeFinishesOnceAfterUnloadedTimeAndDoesNotKeepAnimating()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        string snapshot = "";
        EntityUid destination = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            destination = entities.GetComponent<TransformComponent>(map.Grid).MapUid!.Value;
            var lathe = entities.SpawnEntity("Autolathe", map.GridCoords);
            var component = entities.GetComponent<LatheComponent>(lathe);
            var recipe = pair.Server.ResolveDependency<IPrototypeManager>().Index<LatheRecipePrototype>("Screwdriver");
            component.CurrentRecipe = recipe;
            var active = entities.EnsureComponent<LatheProducingComponent>(lathe);
            active.Recipe = recipe.ID;
            active.ProductionLength = TimeSpan.FromSeconds(5);
            active.StartTime = pair.Server.ResolveDependency<IGameTiming>().CurTime;
            entities.GetComponent<ApcPowerReceiverComponent>(lathe).NeedsPower = false;
            var parking = entities.System<ParkingSystem>();
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            snapshot = parking.SerializeSnapshot(map.Grid);
            entities.DeleteEntity(map.Grid);
        });
        await pair.RunSeconds(30);
        EntityUid restored = default, latheId = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            restored = entities.System<ParkingSystem>().LoadSnapshot(snapshot);
            var entry = entities.EntityQuery<LatheComponent, TransformComponent>(true)
                .Single(entry => entry.Item2.GridUid == restored);
            latheId = entry.Item1.Owner;
            Assert.That(entry.Item1.CurrentRecipe?.ID, Is.EqualTo("Screwdriver"));
            Assert.That(entities.GetComponent<LatheProducingComponent>(latheId).ProductionLength, Is.EqualTo(TimeSpan.FromSeconds(5)));
            entities.System<SharedTransformSystem>().SetCoordinates(restored, new EntityCoordinates(destination, new Vector2(10, 0)));
        });
        await pair.RunSeconds(6);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.HasComponent<LatheProducingComponent>(latheId), Is.False);
            Assert.That(entities.GetComponent<LatheComponent>(latheId).CurrentRecipe, Is.Null);
            Assert.That(entities.EntityQuery<MetaDataComponent, TransformComponent>(true)
                .Count(entry => entry.Item2.GridUid == restored && entry.Item1.EntityPrototype?.ID == "Screwdriver"), Is.EqualTo(1));
            entities.DeleteEntity(restored);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AmeLegacyRoundClockIsBoundedAfterLoad()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var controller = entities.SpawnEntity("AmeController", map.GridCoords);
            var ame = entities.GetComponent<AmeControllerComponent>(controller);
#pragma warning disable RA0002 // Seed an old round's absolute deadline to exercise legacy snapshot recovery.
            ame.NextUpdate = TimeSpan.FromHours(12);
#pragma warning restore RA0002
            var parking = entities.System<ParkingSystem>();
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            var restored = parking.LoadSnapshot(parking.SerializeSnapshot(map.Grid));
            var loaded = entities.EntityQuery<AmeControllerComponent, TransformComponent>(true)
                .Single(entry => entry.Item2.GridUid == restored).Item1;
            Assert.That(loaded.NextUpdate, Is.LessThanOrEqualTo(pair.Server.ResolveDependency<IGameTiming>().CurTime + loaded.UpdatePeriod));
            entities.DeleteEntity(restored);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LoadedReclaimerProcessesAnItemAndProducesMaterials()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        EntityUid restored = default, reclaimer = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var machine = entities.SpawnEntity("MaterialReclaimer", map.GridCoords);
            entities.GetComponent<ApcPowerReceiverComponent>(machine).NeedsPower = false;
            var parking = entities.System<ParkingSystem>();
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            var snapshot = parking.SerializeSnapshot(map.Grid);
            var destination = entities.GetComponent<TransformComponent>(map.Grid).MapUid!.Value;
            entities.DeleteEntity(map.Grid);
            restored = parking.LoadSnapshot(snapshot);
            entities.System<SharedTransformSystem>().SetCoordinates(restored, new EntityCoordinates(destination, new Vector2(10, 0)));
            reclaimer = entities.EntityQuery<MaterialReclaimerComponent, TransformComponent>(true)
                .Single(entry => entry.Item2.GridUid == restored).Item1.Owner;
        });
        await pair.RunSeconds(1);
        EntityUid item = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            item = entities.SpawnEntity("Screwdriver", entities.GetComponent<TransformComponent>(reclaimer).Coordinates);
            Assert.That(entities.System<MaterialReclaimerSystem>().TryStartProcessItem(reclaimer, item), Is.True);
        });
        await pair.RunSeconds(5);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.EntityExists(item), Is.False);
            Assert.That(entities.GetComponent<MaterialReclaimerComponent>(reclaimer).ItemsProcessed, Is.EqualTo(1));
            Assert.That(entities.HasComponent<ActiveMaterialReclaimerComponent>(reclaimer), Is.False);
            var remainder = entities.GetComponent<MaterialStorageComponent>(reclaimer).Storage.Values.Sum();
            var output = entities.EntityQuery<MaterialComponent, TransformComponent>(true).Count(entry => entry.Item2.GridUid == restored);
            Assert.That(remainder > 0 || output > 0, Is.True, "Reclaiming must produce material, not just delete the input.");
            entities.DeleteEntity(restored);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AmeProducesPowerAfterUnloadingAndTwoRestoreCycles()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        EntityUid grid = map.Grid, controller = default, destination = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            destination = entities.GetComponent<TransformComponent>(grid).MapUid!.Value;
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var x = 0; x < 4; x++)
                for (var y = 0; y < 3; y++)
                {
                    maps.SetTile(grid, entities.GetComponent<MapGridComponent>(grid), new Vector2i(x, y), floor);
                    if (x < 3)
                        entities.SpawnEntity("AmeShielding", new EntityCoordinates(grid, new Vector2(x + .5f, y + .5f)));
                }
            controller = entities.SpawnEntity("AmeController", new EntityCoordinates(grid, new Vector2(3.5f, 1.5f)));
            var ame = entities.GetComponent<AmeControllerComponent>(controller);
#pragma warning disable RA0002 // Shorten only this fixture's injection period; all power/fuel changes use the real AME system.
            ame.UpdatePeriod = TimeSpan.FromSeconds(1);
#pragma warning restore RA0002
            var fuel = entities.SpawnEntity("AmeJar", new EntityCoordinates(grid, Vector2.One));
            var containers = entities.System<SharedContainerSystem>();
            Assert.That(containers.Insert(fuel, containers.GetContainer(controller, SharedAmeControllerComponent.FuelSlotId)), Is.True);
            entities.System<AmeControllerSystem>().SetInjecting(controller, true);
            entities.System<ParkingSystem>().RegisterPurchasedShip(grid, Guid.NewGuid(), 1000, "", "Captain");
        });
        await pair.RunSeconds(2);
        for (var cycle = 0; cycle < 2; cycle++)
        {
            string snapshot = "";
            await pair.Server.WaitAssertion(() =>
            {
                var entities = pair.Server.EntMan;
                Assert.That(entities.GetComponent<PowerSupplierComponent>(controller).MaxSupply, Is.GreaterThan(0));
                snapshot = entities.System<ParkingSystem>().SerializeSnapshot(grid);
                entities.DeleteEntity(grid);
            });
            await pair.RunSeconds(20);
            var previousFuel = 0;
            await pair.Server.WaitAssertion(() =>
            {
                var entities = pair.Server.EntMan;
                grid = entities.System<ParkingSystem>().LoadSnapshot(snapshot);
                controller = entities.EntityQuery<AmeControllerComponent, TransformComponent>(true)
                    .Single(entry => entry.Item2.GridUid == grid).Item1.Owner;
                previousFuel = entities.GetComponent<AmeFuelContainerComponent>(entities.GetComponent<AmeControllerComponent>(controller).FuelSlot.Item!.Value).FuelAmount;
                entities.System<SharedTransformSystem>().SetCoordinates(grid, new EntityCoordinates(destination, new Vector2(10, 0)));
            });
            await pair.RunSeconds(2);
            await pair.Server.WaitAssertion(() =>
            {
                var entities = pair.Server.EntMan;
                Assert.That(entities.GetComponent<PowerSupplierComponent>(controller).MaxSupply, Is.GreaterThan(0));
                var fuel = entities.GetComponent<AmeFuelContainerComponent>(entities.GetComponent<AmeControllerComponent>(controller).FuelSlot.Item!.Value);
                Assert.That(fuel.FuelAmount, Is.LessThan(previousFuel), "Fuel injection must actually resume in the rebuilt node group.");
            });
        }
        await pair.Server.WaitAssertion(() => pair.Server.EntMan.DeleteEntity(grid));
        await pair.CleanReturnAsync();
    }
}

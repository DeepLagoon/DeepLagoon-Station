using System;
using System.Collections.Generic;
using System.Reflection;
using Content.Server._EinsteinEngines.Silicon.WeldingHealable;
using Content.Server._EinsteinEngines.Silicon.WeldingHealing;
using Content.Server._NF.PacifiedZone;
using Content.Server.DeviceNetwork.Systems;
using Content.Server.Examine;
using Content.Server.Shuttles.Systems;
using Content.Server.Shuttles.Components;
using Content.Shared._Mono.VendingMachine;
using Content.Shared.CombatMode.Pacification;
using Content.Shared.Damage;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.Lathe;
using Content.Shared.Materials;
using Content.Shared.Mind;
using Content.Shared.Projectiles;
using Content.Shared.Research.Prototypes;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

#pragma warning disable RA0002 // Install stale state from the production runtimes.
[TestFixture]
public sealed class ServerRuntimeRegressionTests
{
    [Test]
    public async Task NetworkStatesPreserveLiveReferencesAndOmitDeletedReferences()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var source = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            var uid = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            var projectile = entities.AddComponent<ProjectileComponent>(uid);
            projectile.Shooter = source;
            projectile.Weapon = source;
            projectile.IgnoreShooter = false;
            var purchase = entities.AddComponent<VendingMachinePurchaseComponent>(uid);
            purchase.PurchaseGrid = source;
            purchase.OriginalPurchasePrice = 125;

            ProjectileComponent.ProjectileComponent_AutoState ProjectileState()
            {
                var ev = new ComponentGetState(null, GameTick.Zero);
                entities.EventBus.RaiseComponentEvent(uid, projectile, ref ev);
                return (ProjectileComponent.ProjectileComponent_AutoState) ev.State;
            }
            VendingMachinePurchaseComponent.VendingMachinePurchaseComponent_AutoState PurchaseState()
            {
                var ev = new ComponentGetState(null, GameTick.Zero);
                entities.EventBus.RaiseComponentEvent(uid, purchase, ref ev);
                return (VendingMachinePurchaseComponent.VendingMachinePurchaseComponent_AutoState) ev.State;
            }
            var sourceNet = entities.GetNetEntity(source);
            Assert.That(ProjectileState().Shooter, Is.EqualTo(sourceNet));
            Assert.That(ProjectileState().Weapon, Is.EqualTo(sourceNet));
            Assert.That(PurchaseState().PurchaseGrid, Is.EqualTo(sourceNet));
            entities.DeleteEntity(source);
            var state = ProjectileState();
            Assert.That(state.Shooter, Is.Null);
            Assert.That(state.Weapon, Is.Null);
            Assert.That(state.IgnoreShooter, Is.False);
            Assert.That(PurchaseState().PurchaseGrid, Is.EqualTo(NetEntity.Invalid));
            Assert.That(PurchaseState().OriginalPurchasePrice, Is.EqualTo(125));
            // Building a PVS/replay state must not mutate authoritative attribution or pricing.
            Assert.That(projectile.Shooter, Is.EqualTo(source));
            Assert.That(purchase.PurchaseGrid, Is.EqualTo(source));
            var projectileHandle = new ComponentHandleState(state, null);
            entities.EventBus.RaiseComponentEvent(uid, projectile, ref projectileHandle);
            Assert.That(projectile.Shooter, Is.Null);
            Assert.That(projectile.Weapon, Is.Null);
            var purchaseHandle = new ComponentHandleState(PurchaseState(), null);
            entities.EventBus.RaiseComponentEvent(uid, purchase, ref purchaseHandle);
            Assert.That(purchase.PurchaseGrid, Is.EqualTo(EntityUid.Invalid));
            Assert.That(purchase.OriginalPurchasePrice, Is.EqualTo(125));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DockedFtlRequestsEnterStartupInsteadOfAvailable()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var destination = maps.CreateMap(out _);
            entities.AddComponent<FTLDestinationComponent>(destination).Enabled = true;
            var shuttle = entities.EnsureComponent<ShuttleComponent>(map.Grid);
            var shuttles = entities.System<ShuttleSystem>();
            var random = pair.Server.ResolveDependency<IRobustRandom>();
            var proximity = typeof(ShuttleSystem).GetMethod("TryGetFTLProximity", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var seed = 0; seed < 32; seed++)
            {
                random.SetSeed(seed);
                object?[] arguments = { map.Grid.Owner, map.GridCoords, null, null, 0f, 64f, null, null };
                Assert.That(proximity.Invoke(shuttles, arguments), Is.True, $"FTL proximity failed for seed {seed}");
            }
            shuttles.FTLToDock(map.Grid, shuttle, destination, startupTime: 10, hyperspaceTime: 30);
            var ftl = entities.GetComponent<FTLComponent>(map.Grid);
            Assert.That(ftl.State, Is.EqualTo(FTLState.Starting));
            Assert.That(ftl.StartupTime, Is.EqualTo(10));
            Assert.That(ftl.TravelTime, Is.EqualTo(30));
            Assert.That(ftl.StateTime.End, Is.GreaterThan(pair.Server.ResolveDependency<IGameTiming>().CurTime));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task QueuedMaterialsMissingFromStorageRemainReserved()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var uid = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            var storage = entities.AddComponent<MaterialStorageComponent>(uid);
            var lathe = entities.AddComponent<LatheComponent>(uid);
            var recipe = new LatheRecipePrototype { Materials = new() { ["RawQuartz"] = 10 } };
            lathe.Queue.Add(new LatheRecipeBatch(recipe, 1, 3, null));
            var remaining = entities.System<SharedLatheSystem>().GetEndMaterialAmounts((uid, lathe));
            Assert.That(remaining["RawQuartz"], Is.EqualTo(-20));
            Assert.That(storage.Storage, Is.Empty);
            lathe.Queue.Clear();
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task WeldingIgnoresUnsupportedDamageButStillFindsRepairableDamage()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var uid = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            var damage = entities.AddComponent<DamageableComponent>(uid);
            damage.Damage.DamageDict.Clear();
            damage.Damage.DamageDict["Blunt"] = 0;
            var healing = new WeldingHealingComponent { Damage = new DamageSpecifier() };
            healing.Damage.DamageDict["Radiation"] = -5;
            healing.Damage.DamageDict["Blunt"] = -5;
            Entity<DamageableComponent> target = (uid, damage);
            var system = entities.System<WeldingHealableSystem>();
            var method = typeof(WeldingHealableSystem).GetMethod("HasDamage", BindingFlags.Instance | BindingFlags.NonPublic)!;
            bool HasDamage() => (bool) method.Invoke(system, new object[] { target, healing, uid })!;
            Assert.That(HasDamage(), Is.False);
            damage.Damage.DamageDict["Blunt"] = 10;
            Assert.That(HasDamage(), Is.True);
            Assert.That(damage.Damage.DamageDict.ContainsKey("Radiation"), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PacifiedZoneReusesExistingZoneMarker()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var mob = entities.SpawnEntity("MobHuman", map.GridCoords);
            var minds = entities.System<SharedMindSystem>();
            var mind = minds.CreateMind(null);
            minds.TransferTo(mind, mob);
            var marker = entities.AddComponent<PacifiedByZoneComponent>(mob);
            var generator = entities.SpawnEntity(null, map.GridCoords);
            entities.AddComponent<PacifiedZoneGeneratorComponent>(generator);
            Assert.That(entities.GetComponent<PacifiedByZoneComponent>(mob), Is.SameAs(marker));
            Assert.That(entities.HasComponent<PacifiedComponent>(mob), Is.True);
            entities.DeleteEntity(generator);
            Assert.That(entities.HasComponent<PacifiedByZoneComponent>(mob), Is.False);
            Assert.That(entities.HasComponent<PacifiedComponent>(mob), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RevolverDiscardsDeletedChamberReferences(bool firing)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var uid = entities.SpawnEntity(null, map.GridCoords);
            var revolver = entities.AddComponent<RevolverAmmoProviderComponent>(uid);
            var stale = entities.SpawnEntity(null, map.GridCoords);
            entities.DeleteEntity(stale);
            Array.Fill(revolver.Chambers, (bool?) null);
            revolver.AmmoSlots[0] = stale;
            revolver.Chambers[0] = true;
            var ev = new TakeAmmoEvent(1, new(), map.GridCoords, null, willBeFired: firing);
            entities.EventBus.RaiseLocalEvent(uid, ev);
            Assert.That(ev.Ammo, Is.Empty);
            Assert.That(revolver.AmmoSlots[0], Is.Null);
            Assert.That(revolver.Chambers[0], Is.Null);
            typeof(SharedGunSystem).GetMethod("EjectCartridge", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(entities.System<SharedGunSystem>(), new object?[] { stale, null, false });
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("NuclearReactorNormal")]
    [TestCase("NuclearReactorMelted")]
    [TestCase("NuclearReactorSmallMeltedSalvage")]
    [TestCase("NuclearReactorSmall")]
    [TestCase("Turbine")]
    public async Task ExaminingIndestructiblePowerEquipmentDoesNotRequireDamageComponents(string prototype)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var uid = entities.SpawnEntity(prototype, map.GridCoords);
            entities.System<ExamineSystem>().GetExamineText(uid, uid);
            Assert.That(entities.HasComponent<DamageableComponent>(uid), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DeviceShutdownAcceptsAListThatWasAlreadyDeleted()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var uid = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            var device = entities.AddComponent<DeviceNetworkComponent>(uid);
            var stale = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            entities.DeleteEntity(stale);
            device.DeviceLists.Add(stale);
            entities.System<DeviceListSystem>().OnDeviceShutdown(stale, (uid, device));
            Assert.That(device.DeviceLists, Is.Empty);
        });
        await pair.CleanReturnAsync();
    }
}

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Content.Server.Database;
using Content.Server._DeepLagoon.CrashRecovery;
using Content.Shared.Implants;
using Content.Shared.Implants.Components;
using Content.Shared.Inventory;
using Content.Shared.Storage;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Components;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class CrashRecoveryIntegrationTests
{
    [Test]
    public async Task ContinuousContainerChangesCannotStarveSnapshots()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var scope = "test" + Guid.NewGuid().ToString("N");
        CrashRecoverySystem recovery = default!;
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(RecoveryCVars.Scope, scope);
            pair.Server.CfgMan.SetCVar(RecoveryCVars.Enabled, true);
            recovery = pair.Server.EntMan.System<CrashRecoverySystem>();
        });
        await PoolManager.WaitUntil(pair.Server, () => recovery.Ready, 3600);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var containers = e.System<SharedContainerSystem>();
            var body = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            recovery.RegisterBody(body, Guid.NewGuid(), 1);
            var bag = e.SpawnEntity("ClothingBackpack", map.GridCoords);
            Assert.That(e.System<InventorySystem>().TryEquip(body, bag, "back", force: true), Is.True);
            var box = e.SpawnEntity("BoxCardboard", map.GridCoords);
            Assert.That(containers.Insert(box, e.GetComponent<StorageComponent>(bag).Container), Is.True);
            // Exercise containment events independently of storage UI placement/capacity rules.
            var left = containers.EnsureContainer<Container>(bag, "recovery-regression-left");
            var right = containers.EnsureContainer<Container>(box, "recovery-regression-right");
            var moving = e.SpawnEntity("Pen", map.GridCoords);
            Assert.That(containers.Insert(moving, left), Is.True);
            Assert.That(containers.Insert(e.SpawnEntity("Pen", map.GridCoords), left), Is.True);
            for (var tick = 0; tick < 600; tick++)
            {
                Assert.That(containers.TryGetContainingContainer(moving, out var current), Is.True);
                Assert.That(containers.Remove(moving, current, force: true), Is.True);
                Assert.That(containers.Insert(moving, tick % 2 == 0 ? right : left), Is.True);
                recovery.Update(1f / 30);
            }
            Assert.That(recovery.CapturedCount, Is.GreaterThanOrEqualTo(4));
            Assert.That(recovery.PendingCount, Is.LessThan(256), "Repeated parent scans must be deduplicated.");
        });
        await recovery.CloseNormally();
        await recovery.StopWorker();
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ApartmentEscrowKeepsOfflineCharacterAndOriginalNestedReturnPaths()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var scope = "test" + Guid.NewGuid().ToString("N");
        var user = Guid.NewGuid();
        CrashRecoverySystem recovery = default!;
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(RecoveryCVars.Scope, scope);
            pair.Server.CfgMan.SetCVar(RecoveryCVars.Enabled, true);
            recovery = pair.Server.EntMan.System<CrashRecoverySystem>();
        });
        await PoolManager.WaitUntil(pair.Server, () => recovery.Ready, 3600);
        EntityUid originalDisk = default, apartmentDisk = default, secondDisk = default, temporaryBag = default;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var containers = e.System<SharedContainerSystem>();
            var body = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            var escrow = e.SpawnEntity("CrateGenericSteel", map.GridCoords);
            recovery.RegisterEscrow(escrow, body); // Apartment entry can precede the async profile lookup.
            var bag = e.SpawnEntity("ClothingBackpack", map.GridCoords);
            Assert.That(containers.Insert(bag, e.GetComponent<Content.Server.Storage.Components.EntityStorageComponent>(escrow).Contents), Is.True);
            e.GetComponent<RecoveryEscrowComponent>(escrow).ReturnPaths[bag] = ["back"];
            originalDisk = e.SpawnEntity("ResearchDisk", map.GridCoords);
            Assert.That(containers.Insert(originalDisk, e.GetComponent<StorageComponent>(bag).Container), Is.True);
            temporaryBag = e.SpawnEntity("ClothingBackpack", map.GridCoords);
            e.AddComponent<Content.Server._DeepLagoon.Apartments.ApartmentChameleonGearComponent>(temporaryBag);
            Assert.That(e.System<InventorySystem>().TryEquip(body, temporaryBag, "back", force: true), Is.True);
            apartmentDisk = e.SpawnEntity("ResearchDisk", map.GridCoords);
            Assert.That(containers.Insert(apartmentDisk, e.GetComponent<StorageComponent>(temporaryBag).Container), Is.True);
            recovery.RegisterBody(body, user, 1);
            var other = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            recovery.RegisterBody(other, user, 2); // Same account, different immutable character.
            var otherBag = e.SpawnEntity("ClothingBackpack", map.GridCoords);
            Assert.That(e.System<InventorySystem>().TryEquip(other, otherBag, "back", force: true), Is.True);
            secondDisk = e.SpawnEntity("ResearchDisk", map.GridCoords);
            Assert.That(containers.Insert(secondDisk, e.GetComponent<StorageComponent>(otherBag).Container), Is.True);
            e.AddComponent<Content.Shared._NF.Shipyard.Components.ShipOwnershipComponent>(map.Grid).OwnerUserId = new Robust.Shared.Network.NetUserId(user);
            recovery.RegisterOwnedShip(map.Grid, other);
            var researchServer = e.SpawnEntity("ResearchAndDevelopmentServer", map.GridCoords);
            var research = e.GetComponent<Content.Shared.Research.Components.ResearchServerComponent>(researchServer);
            e.System<Content.Server.Research.Systems.ResearchSystem>().ModifyServerPoints(researchServer, 47382 - research.Points, research);
            recovery.BindPurchasedShip(recovery.BeginPurchase(body, 100), map.Grid);
            var destination = e.System<SharedMapSystem>().CreateMap();
            e.System<SharedTransformSystem>().SetCoordinates(map.Grid, new EntityCoordinates(destination, 0, 0));
            // Moving a purchased grid must not enroll the whole vessel as personal equipment.
            e.System<SharedMapSystem>().SetPaused(destination, true);
            e.System<SharedMapSystem>().SetPaused(map.MapUid, true);
        });
        RecoveryIncident snapshot = default!;
        await PoolManager.WaitUntil(pair.Server, async () =>
        {
            snapshot = await pair.Server.ResolveDependency<IServerDbManager>().ReadCrashRecovery(scope, recovery.RunIdentity);
            return snapshot.Assets.Count(a => a.Data.Length > 0) >= 5 && snapshot.Assets.Any(a => a.Kind == "research");
        }, 1200);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            RecoveryAsset Saved(EntityUid uid) => snapshot.Assets.Single(a => a.Identity == e.GetComponent<RecoveryIdentityComponent>(uid).Identity);
            Assert.That(Saved(originalDisk).BodyPath, Is.EqualTo(new[] { "back", StorageComponent.ContainerId }));
            Assert.That(Saved(originalDisk).Profile, Is.EqualTo(1));
            Assert.That(Saved(apartmentDisk).Profile, Is.EqualTo(1));
            Assert.That(Saved(secondDisk).Profile, Is.EqualTo(2));
            Assert.That(e.HasComponent<RecoveryIdentityComponent>(temporaryBag), Is.False);
            Assert.That(snapshot.Assets.Count(a => a.Kind == "item"), Is.EqualTo(5));
            var research = snapshot.Assets.Single(a => a.Kind == "research");
            Assert.That(research.Amount, Is.EqualTo(47382));
            Assert.That(research.Profile, Is.EqualTo(2));
        });
        await recovery.CloseNormally();
        await recovery.StopWorker();
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SeventyOfflineBodiesIncludingPausedStorageCaptureNestedGearAndImplants()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var scope = "test" + Guid.NewGuid().ToString("N");
        CrashRecoverySystem recovery = default!;
        var expected = 0;
        var samples = new List<double>();
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(RecoveryCVars.Scope, scope);
            pair.Server.CfgMan.SetCVar(RecoveryCVars.Enabled, true);
            recovery = pair.Server.EntMan.System<CrashRecoverySystem>();
        });
        await PoolManager.WaitUntil(pair.Server, () => recovery.Ready, 3600);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var containers = e.System<SharedContainerSystem>();
            var inventory = e.System<InventorySystem>();
            var maps = e.System<SharedMapSystem>();
            var paused = maps.CreateMap();
            maps.SetPaused(paused, true);
            for (var i = 0; i < 70; i++)
            {
                var body = e.SpawnEntity("MobHumanDummy", map.GridCoords);
                recovery.RegisterBody(body, Guid.NewGuid(), i + 1);
                var bag = e.SpawnEntity("ClothingBackpack", map.GridCoords);
                Assert.That(inventory.TryEquip(body, bag, "back", force: true), Is.True);
                var outer = e.GetComponent<StorageComponent>(bag).Container;
                var nested = e.SpawnEntity("BoxCardboard", map.GridCoords);
                Assert.That(containers.Insert(nested, outer), Is.True);
                var inner = e.GetComponent<StorageComponent>(nested).Container;
                for (var j = 0; j < 5; j++)
                {
                    var disk = e.SpawnEntity("Pen", map.GridCoords);
                    Assert.That(containers.Insert(disk, inner), Is.True);
                }
                for (var j = 0; j < 10; j++)
                    Assert.That(containers.Insert(e.SpawnEntity("Pen", map.GridCoords), outer), Is.True);
                var implant = e.SpawnEntity("StorageImplant", map.GridCoords);
                e.System<SharedSubdermalImplantSystem>().ForceImplant(body, implant, e.GetComponent<SubdermalImplantComponent>(implant));
                expected += 18;
                // No sessions/ActorComponents exist. Half the bodies are also on a paused map.
                if (i % 2 == 0) e.System<SharedTransformSystem>().SetCoordinates(body, new EntityCoordinates(paused, i, 0));
            }
        });
        for (var block = 0; block < 100 && recovery.CapturedCount < expected; block++)
        {
            await pair.Server.WaitAssertion(() =>
            {
                for (var tick = 0; tick < 100; tick++)
                {
                    recovery.Update(1f / 60);
                    samples.Add(recovery.LastTickMilliseconds);
                }
            });
            await pair.RunTicksSync(2);
        }
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(recovery.CapturedCount, Is.GreaterThanOrEqualTo(expected));
            Assert.That(recovery.PendingCount, Is.LessThan(8192));
        });
        samples.Sort();
        TestContext.WriteLine($"70 offline bodies / {expected} protected entities: ticks={samples.Count}; p50_ms={samples[samples.Count / 2]:F3}; p99_ms={samples[(int)(samples.Count * .99)]:F3}; max_ms={samples[^1]:F3}; max_capture_ms={recovery.MaxCaptureMilliseconds:F3}; slowest={recovery.MaxCapturePrototype}; collections_during_slowest={recovery.MaxCaptureCollections}");
        await recovery.CloseNormally();
        await recovery.StopWorker();
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task IncrementalEntitySnapshotsRoundTripNestedContainerWithCustomResearchDiskPoints()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var scope = "test" + Guid.NewGuid().ToString("N");
        CrashRecoverySystem recovery = default!;
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(RecoveryCVars.Scope, scope);
            pair.Server.CfgMan.SetCVar(RecoveryCVars.Enabled, true);
            recovery = pair.Server.EntMan.System<CrashRecoverySystem>();
        });
        await PoolManager.WaitUntil(pair.Server, () => recovery.Ready, 3600);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var body = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            recovery.RegisterBody(body, Guid.NewGuid(), 1);
            var bag = e.SpawnEntity("ClothingBackpack", map.GridCoords);
            Assert.That(e.System<InventorySystem>().TryEquip(body, bag, "back", force: true), Is.True);
            var disk = e.SpawnEntity("ResearchDisk", map.GridCoords);
            e.GetComponent<Content.Server.Research.Disk.ResearchDiskComponent>(disk).Points = 47382;
            Assert.That(e.System<SharedContainerSystem>().Insert(disk, e.GetComponent<StorageComponent>(bag).Container), Is.True);
            var legacy = e.SpawnEntity("SpaceMedipen", map.GridCoords);
            Assert.That(e.System<SharedContainerSystem>().Insert(legacy, e.GetComponent<StorageComponent>(bag).Container), Is.True);
            var implant = e.SpawnEntity("StorageImplant", map.GridCoords);
            e.System<SharedSubdermalImplantSystem>().ForceImplant(body, implant, e.GetComponent<SubdermalImplantComponent>(implant));
            var action = e.GetComponent<Content.Shared.Actions.InstantActionComponent>(e.GetComponent<SubdermalImplantComponent>(implant).Action!.Value);
            action.Charges = 2;
            action.MaxCharges = 7;
            action.Enabled = false;
            var implantedDisk = e.SpawnEntity("ResearchDisk", map.GridCoords);
            e.GetComponent<Content.Server.Research.Disk.ResearchDiskComponent>(implantedDisk).Points = 137;
            Assert.That(e.System<SharedContainerSystem>().Insert(implantedDisk, e.GetComponent<StorageComponent>(implant).Container), Is.True);
        });
        RecoveryIncident snapshot = default!;
        await PoolManager.WaitUntil(pair.Server, async () =>
        {
            snapshot = await pair.Server.ResolveDependency<IServerDbManager>().ReadCrashRecovery(scope, recovery.RunIdentity);
            return snapshot.Assets.Any(a => a.Prototype == "SpaceMedipen" && a.Data.Length > 0)
                && snapshot.Assets.Any(a => a.Prototype == "ClothingBackpack" && a.Data.Length > 0)
                && snapshot.Assets.Any(a => a.Prototype == "StorageImplant" && a.Data.Length > 0)
                && snapshot.Assets.Count(a => a.Prototype == "ResearchDisk" && a.Data.Length > 0) == 2;
        }, 1200);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            Assert.That(snapshot.Assets.Any(a => a.Data.Contains("!recovery-ref")), Is.True,
                "References must use immutable identities across repeated crashes and incident merges.");
            // Claimed assets can retain overlapping YAML numbers from different crashed runs.
            Assert.That(recovery.TryLoadRecoveryEntities(snapshot.Assets.Select(a => a with { YamlId = 1 }).ToArray(), out var result), Is.True);
            Assert.That(result!.Entities.Count(uid => e.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "SpaceMedipen"), Is.EqualTo(1),
                "A valid runtime prototype must survive a map-migration deletion entry.");
            var loadedBag = result!.Entities.Single(uid => e.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "ClothingBackpack");
            var disk = e.GetComponent<StorageComponent>(loadedBag).Container.ContainedEntities.Single(uid => e.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "ResearchDisk");
            Assert.That(e.GetComponent<Content.Server.Research.Disk.ResearchDiskComponent>(disk).Points, Is.EqualTo(47382));
            var loadedImplant = result.Entities.Single(e.HasComponent<SubdermalImplantComponent>);
            var receiver = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            var implant = e.GetComponent<SubdermalImplantComponent>(loadedImplant);
            e.System<SharedSubdermalImplantSystem>().ForceImplant(receiver, loadedImplant, implant);
            recovery.RestoreImplantState(loadedImplant);
            Assert.That(implant.ImplantedEntity, Is.EqualTo(receiver));
            Assert.That(implant.Action, Is.Not.Null, "Implant actions must be regenerated for the new body.");
            var action = e.GetComponent<Content.Shared.Actions.InstantActionComponent>(implant.Action!.Value);
            Assert.That(action.Charges, Is.EqualTo(2));
            Assert.That(action.MaxCharges, Is.EqualTo(7));
            Assert.That(action.Enabled, Is.False);
            var implantedDisk = e.GetComponent<StorageComponent>(loadedImplant).Container.ContainedEntities.Single();
            Assert.That(e.GetComponent<Content.Server.Research.Disk.ResearchDiskComponent>(implantedDisk).Points, Is.EqualTo(137));
        });
        await recovery.CloseNormally();
        await recovery.StopWorker();
        await pair.CleanReturnAsync();
    }
}

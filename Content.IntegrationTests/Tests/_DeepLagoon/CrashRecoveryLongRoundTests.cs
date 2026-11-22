using System.Linq;
using Content.Server.Database;
using Content.Server._DeepLagoon.CrashRecovery;
using Content.Shared.Body.Part;
using Content.Shared.Humanoid;
using Content.Shared._Shitmed.Body.Part;
using Content.Shared.Storage;
using Content.Shared.Instruments;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class CrashRecoveryLongRoundTests
{
    private static async Task<CrashRecoverySystem> Enable(Pair.TestPair pair, string scope)
    {
        CrashRecoverySystem recovery = default!;
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(RecoveryCVars.Scope, scope);
            pair.Server.CfgMan.SetCVar(RecoveryCVars.Enabled, true);
            recovery = pair.Server.EntMan.System<CrashRecoverySystem>();
        });
        await PoolManager.WaitUntil(pair.Server, () => recovery.Ready, 3600);
        return recovery;
    }

    [Test]
    public async Task DeletingAContainerDuringDiscoveryDoesNotStarveTheRemainingEquipment()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var scope = "test" + Guid.NewGuid().ToString("N");
        var recovery = await Enable(pair, scope);
        EntityUid outer = default, inner = default;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var containers = e.System<SharedContainerSystem>();
            var body = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            recovery.RegisterBody(body, Guid.NewGuid(), 1);
            outer = e.SpawnEntity("ClothingBackpack", map.GridCoords);
            Assert.That(e.System<Content.Shared.Inventory.InventorySystem>().TryEquip(body, outer, "back", force: true), Is.True);
            inner = e.SpawnEntity("BoxCardboard", map.GridCoords);
            Assert.That(containers.Insert(inner, e.GetComponent<StorageComponent>(outer).Container), Is.True);
            // Exercise the incremental (>64 children) enumerator without depending on item-grid capacity.
            var contents = containers.EnsureContainer<Container>(inner, "recovery-deletion-contents");
            for (var i = 0; i < 100; i++) Assert.That(containers.Insert(e.SpawnEntity("Pen", map.GridCoords), contents), Is.True);
        });
        await PoolManager.WaitUntil(pair.Server, () => pair.Server.EntMan.HasComponent<RecoveryIdentityComponent>(inner), 600);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            e.DeleteEntity(inner);
            var pen = e.SpawnEntity("Pen", map.GridCoords);
            e.System<MetaDataSystem>().SetEntityName(pen, "surviving-recovery-pen");
            Assert.That(e.System<SharedContainerSystem>().Insert(pen, e.GetComponent<StorageComponent>(outer).Container), Is.True);
        });
        await PoolManager.WaitUntil(pair.Server, async () =>
        {
            var snapshot = await pair.Server.ResolveDependency<IServerDbManager>().ReadCrashRecovery(scope, recovery.RunIdentity);
            return snapshot.Assets.Any(a => a.Location == RecoveryLocation.Carried && a.Prototype == "ClothingBackpack" && a.Data.Length > 0)
                && snapshot.Assets.Any(a => a.Location == RecoveryLocation.Carried && a.Data.Contains("surviving-recovery-pen"));
        }, 1800);
        await recovery.CloseNormally();
        await recovery.StopWorker();
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ApartmentGearStillGetsItsOwnerWhenTheOriginalBodyWasDeletedDuringProfileLookup()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var scope = "test" + Guid.NewGuid().ToString("N");
        var user = Guid.NewGuid();
        var recovery = await Enable(pair, scope);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var body = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            var escrow = e.SpawnEntity("CrateGenericSteel", map.GridCoords);
            recovery.RegisterEscrow(escrow, body);
            var bag = e.SpawnEntity("ClothingBackpack", map.GridCoords);
            Assert.That(e.System<SharedContainerSystem>().Insert(bag,
                e.GetComponent<Content.Server.Storage.Components.EntityStorageComponent>(escrow).Contents), Is.True);
            e.GetComponent<RecoveryEscrowComponent>(escrow).ReturnPaths[bag] = ["back"];
            e.DeleteEntity(body);
            recovery.RegisterBody(body, user, 1); // The pending immutable profile lookup finishes later.
        });
        await PoolManager.WaitUntil(pair.Server, async () =>
        {
            var snapshot = await pair.Server.ResolveDependency<IServerDbManager>().ReadCrashRecovery(scope, recovery.RunIdentity);
            return snapshot.Assets.Any(a => a.Prototype == "ClothingBackpack" && a.Data.Length > 0
                && a.Location == RecoveryLocation.Carried && a.User == user && a.Profile == 1 && a.BodyPath.SequenceEqual(new[] { "back" }));
        }, 1800);
        await recovery.CloseNormally();
        await recovery.StopWorker();
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DeletedPurchasesLeaveDurableTombstonesWithoutFillingTheLiveRegistry()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var scope = "test" + Guid.NewGuid().ToString("N");
        var recovery = await Enable(pair, scope);
        const int destroyed = 1024;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            using (recovery.SpawnPurchaseScope(recovery.BeginProfilePurchase(Guid.NewGuid(), 100)))
                for (var i = 0; i < destroyed; i++) e.DeleteEntity(e.SpawnEntity("Pen", map.GridCoords));
            Assert.That(recovery.TrackedCount, Is.Zero, "History must not count toward the live equipment registry.");
            var body = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            recovery.RegisterBody(body, Guid.NewGuid(), 1);
            var bag = e.SpawnEntity("ClothingBackpack", map.GridCoords);
            Assert.That(e.System<Content.Shared.Inventory.InventorySystem>().TryEquip(body, bag, "back", force: true), Is.True);
            Assert.That(e.System<SharedContainerSystem>().Insert(e.SpawnEntity("Pen", map.GridCoords),
                e.GetComponent<StorageComponent>(bag).Container), Is.True);
        });
        await PoolManager.WaitUntil(pair.Server, async () =>
        {
            var snapshot = await pair.Server.ResolveDependency<IServerDbManager>().ReadCrashRecovery(scope, recovery.RunIdentity);
            return snapshot.Assets.Count(a => a.Location == RecoveryLocation.Destroyed) == destroyed
                && snapshot.Assets.Count(a => a.Location == RecoveryLocation.Carried && a.Data.Length > 0) == 2;
        }, 1800);
        await pair.Server.WaitAssertion(() => Assert.That(recovery.TrackedCount, Is.EqualTo(2)));
        await recovery.CloseNormally();
        await recovery.StopWorker();
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AnOpenMidiSessionDoesNotPreventSavingTheOwnedInstrument()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var scope = "test" + Guid.NewGuid().ToString("N");
        var recovery = await Enable(pair, scope);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var body = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            recovery.RegisterBody(body, Guid.NewGuid(), 1);
            var container = e.System<SharedContainerSystem>().EnsureContainer<Container>(body, "recovery-test-equipment");
            var instrument = e.SpawnEntity("KalimbaInstrument", map.GridCoords);
            Assert.That(e.System<SharedContainerSystem>().Insert(instrument, container), Is.True);
            e.EnsureComponent<ActiveInstrumentComponent>(instrument).Tracks = [new MidiTrack { TrackName = "test" }];
        });
        RecoveryIncident snapshot = default!;
        await PoolManager.WaitUntil(pair.Server, async () =>
        {
            snapshot = await pair.Server.ResolveDependency<IServerDbManager>().ReadCrashRecovery(scope, recovery.RunIdentity);
            return snapshot.Assets.Any(a => a.Prototype == "KalimbaInstrument" && a.Data.Length > 0);
        }, 1800);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(recovery.TryLoadRecoveryEntities(snapshot.Assets.Where(a => a.Data.Length > 0).ToArray(), out var loaded), Is.True);
            Assert.That(loaded!.Entities.Any(pair.Server.EntMan.HasComponent<ActiveInstrumentComponent>), Is.False);
        });
        await recovery.CloseNormally();
        await recovery.StopWorker();
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MainEquipmentSnapshotsSurviveRepeatedCapturesAndDeserialization()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var scope = "test" + Guid.NewGuid().ToString("N");
        var recovery = await Enable(pair, scope);
        string[] prototypes = ["LeftFootCybernetic", "RightFootCybernetic", "SpeedLeftLeg", "SpeedRightLeg",
            "JawsOfLifeLeftArm", "JawsOfLifeRightArm", "ClothingBackpackMercenary", "ClothingBackpackSatchelSecurity",
            "ClothingBeltWebbingsBigBlack", "ModModuleStorage", "ClothingOuterHardsuitSpatio"];
        EntityUid[] items = [];
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var body = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            recovery.RegisterBody(body, Guid.NewGuid(), 1);
            var container = e.System<SharedContainerSystem>().EnsureContainer<Container>(body, "recovery-test-equipment");
            items = prototypes.Select(p => e.SpawnEntity(p, map.GridCoords)).ToArray();
            foreach (var item in items)
            {
                Assert.That(e.System<SharedContainerSystem>().Insert(item, container), Is.True);
                if (e.TryGetComponent<BodyPartComponent>(item, out var part))
                {
                    part.Body = body;
                    part.ParentSlot = new BodyPartSlot("recovery-test", part.PartType);
                    var appearance = e.EnsureComponent<BodyPartAppearanceComponent>(item);
                    appearance.Type = HumanoidVisualLayers.Hair;
                    appearance.Color = Color.Cyan;
                }
            }
        });
        RecoveryIncident snapshot = default!;
        async Task<bool> Captured(string marker)
        {
            snapshot = await pair.Server.ResolveDependency<IServerDbManager>().ReadCrashRecovery(scope, recovery.RunIdentity);
            return prototypes.All(p => snapshot.Assets.Any(a => a.Prototype == p && a.Data.Length > 0
                && (marker.Length == 0 || a.Data.Contains(marker))));
        }
        await PoolManager.WaitUntil(pair.Server, () => Captured(""), 1800);
        for (var pass = 0; pass < 2; pass++)
        {
            await pair.RunTicksSync(2);
            await pair.Server.WaitAssertion(() =>
            {
                foreach (var item in items)
                    pair.Server.EntMan.System<MetaDataSystem>().SetEntityName(item, "recovery-repeat-" + pass);
            });
            await PoolManager.WaitUntil(pair.Server, () => Captured("recovery-repeat-" + pass), 1800);
        }
        await pair.Server.WaitAssertion(() =>
        {
            var selected = snapshot.Assets.Where(a => prototypes.Contains(a.Prototype)).ToArray();
            Assert.That(selected.Where(a => a.Prototype.Contains("Cybernetic") || a.Prototype.StartsWith("Speed")
                    || a.Prototype.StartsWith("JawsOfLife")).All(a => a.Data.Contains("visualLayer: Hair")), Is.True);
            Assert.That(recovery.TryLoadRecoveryEntities(selected, out var loaded), Is.True);
            Assert.That(loaded!.Entities.Count, Is.EqualTo(selected.Length));
            foreach (var uid in loaded.Entities.Where(pair.Server.EntMan.HasComponent<BodyPartComponent>))
            {
                var appearance = pair.Server.EntMan.GetComponent<BodyPartAppearanceComponent>(uid);
                // Native startup derives the visual layer from the prosthetic's actual slot.
                Assert.That(appearance.Type, Is.Not.EqualTo(HumanoidVisualLayers.Special));
                Assert.That(appearance.Color, Is.EqualTo(Color.Cyan));
            }
        });
        await recovery.CloseNormally();
        await recovery.StopWorker();
        await pair.CleanReturnAsync();
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task SingleItemSlotsContainOnlyChildrenIncludedInTheRecovery(int mode)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var scope = "test" + Guid.NewGuid().ToString("N");
        var recovery = await Enable(pair, scope);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var body = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            recovery.RegisterBody(body, Guid.NewGuid(), 1);
            var bag = e.SpawnEntity("ClothingBackpack", map.GridCoords);
            Assert.That(e.System<Content.Shared.Inventory.InventorySystem>().TryEquip(body, bag, "back", force: true), Is.True);
            var slot = e.System<SharedContainerSystem>().EnsureContainer<ContainerSlot>(bag, "recovery-test-slot");
            var pen = e.SpawnEntity("Pen", map.GridCoords);
            e.System<MetaDataSystem>().SetEntityName(pen, "slot-recovery-pen");
            Assert.That(e.System<SharedContainerSystem>().Insert(pen, slot), Is.True);
            if (mode == 2)
            {
                var extra = e.SpawnEntity("Pen", map.GridCoords);
                e.System<MetaDataSystem>().SetEntityName(extra, "extra-recovery-pen");
                Assert.That(e.System<SharedContainerSystem>().Insert(extra, e.GetComponent<StorageComponent>(bag).Container), Is.True);
            }
        });
        RecoveryIncident snapshot = default!;
        await PoolManager.WaitUntil(pair.Server, async () =>
        {
            snapshot = await pair.Server.ResolveDependency<IServerDbManager>().ReadCrashRecovery(scope, recovery.RunIdentity);
            return snapshot.Assets.Count(a => a.Data.Length > 0) == (mode == 2 ? 3 : 2);
        }, 1800);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var selected = snapshot.Assets.Where(a => mode != 0 || a.Prototype == "ClothingBackpack")
                .Select(a => mode == 2 && a.Data.Contains("extra-recovery-pen")
                    ? a with { BodyPath = ["back", "recovery-test-slot"] } : a)
                .OrderBy(a => a.Data.Contains("extra-recovery-pen") ? 0 : 1).ToArray();
            Assert.That(recovery.TryLoadRecoveryEntities(selected, out var loaded), Is.True);
            var bag = loaded!.Entities.Single(uid => e.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "ClothingBackpack");
            var slot = e.System<SharedContainerSystem>().GetContainer(bag, "recovery-test-slot");
            Assert.That(slot.ContainedEntities.Count, Is.EqualTo(mode == 0 ? 0 : 1));
            if (mode == 2)
            {
                Assert.That(e.GetComponent<MetaDataComponent>(slot.ContainedEntities.Single()).EntityName, Is.EqualTo("slot-recovery-pen"));
                var extra = loaded.Entities.Single(uid => e.GetComponent<MetaDataComponent>(uid).EntityName == "extra-recovery-pen");
                Assert.That(e.GetComponent<TransformComponent>(extra).ParentUid.IsValid(), Is.False,
                    "Conflicting journal children must survive as independent roots for personal crate delivery.");
            }
        });
        await recovery.CloseNormally();
        await recovery.StopWorker();
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExternalDictionaryReferencesDoNotBlockTheBackpackSnapshot()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var scope = "test" + Guid.NewGuid().ToString("N");
        var recovery = await Enable(pair, scope);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var body = e.SpawnEntity("MobHumanDummy", map.GridCoords);
            recovery.RegisterBody(body, Guid.NewGuid(), 1);
            var bag = e.SpawnEntity("ClothingBackpack", map.GridCoords);
            Assert.That(e.System<Content.Shared.Inventory.InventorySystem>().TryEquip(body, bag, "back", force: true), Is.True);
            var storage = e.GetComponent<StorageComponent>(bag);
            // These can coexist while storage membership and transform events are being processed.
            storage.StoredItems[e.SpawnEntity("Pen", map.GridCoords)] = new ItemStorageLocation(Angle.Zero, Vector2i.Zero);
            storage.StoredItems[e.SpawnEntity("Pen", map.GridCoords)] = new ItemStorageLocation(Angle.Zero, Vector2i.One);
        });
        RecoveryIncident snapshot = default!;
        await PoolManager.WaitUntil(pair.Server, async () =>
        {
            snapshot = await pair.Server.ResolveDependency<IServerDbManager>().ReadCrashRecovery(scope, recovery.RunIdentity);
            return snapshot.Assets.Any(a => a.Prototype == "ClothingBackpack" && a.Data.Length > 0);
        }, 1800);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(recovery.TryLoadRecoveryEntities(snapshot.Assets.Where(a => a.Data.Length > 0).ToArray(), out var loaded), Is.True);
            var bag = loaded!.Entities.Single(e => pair.Server.EntMan.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID == "ClothingBackpack");
            Assert.That(pair.Server.EntMan.GetComponent<StorageComponent>(bag).StoredItems, Is.Empty);
        });
        await recovery.CloseNormally();
        await recovery.StopWorker();
        await pair.CleanReturnAsync();
    }
}

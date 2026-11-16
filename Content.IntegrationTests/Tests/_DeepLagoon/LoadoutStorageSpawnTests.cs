using System.Linq;
using Content.Server.Station.Systems;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.CCVar;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Radio.Components;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class LoadoutStorageSpawnTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task RoleStorageArrivesInThePersonalBag(bool originalBackpack)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.PersonalLoadoutsEnabled, true);
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var inventory = entities.System<InventorySystem>();
            var spawning = entities.System<StationSpawningSystem>();
            var role = new RoleLoadout("JobMercenary");
            role.SelectedLoadouts["MercenaryEars"] = new() { new() { Prototype = "MercenaryClothingHeadsetAltMercenaryCommon" } };
            role.SelectedLoadouts["MercenaryFace"] = new() { new() { Prototype = "MercenaryClothingMaskGasMercenary" } };
            role.SelectedLoadouts["ContractorEncryptionKey"] = new() { new() { Prototype = "ContractorEncryptionKeyTraffic" } };
            role.SelectedLoadouts["ContractorCartridge"] = new() { new() { Prototype = "ContractorAppraisalCartridge" } };
            role.SelectedLoadouts["MercenaryPistol"] = new() { new() { Prototype = "MercenaryPollockLoadout" } };
            if (originalBackpack)
                role.SelectedLoadouts["MercenaryBackpack"] = new() { new() { Prototype = "MercenaryClothingBackpackMercenary" } };

            const string personalBag = "DLEELoadoutItemBackpackSatchelLeather";
            var personal = new RoleLoadout(PersonalLoadoutSystem.Role);
            var group = prototypes.Index<RoleLoadoutPrototype>(PersonalLoadoutSystem.Role).Groups
                .First(g => prototypes.Index(g).Loadouts.Any(item => item.Id == personalBag));
            personal.SelectedLoadouts[group] = new() { new() { Prototype = personalBag } };
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithLoadout(role).WithLoadout(personal);

            var mob = spawning.SpawnPlayerMob(map.GridCoords, "Mercenary", profile, null, deferStartingGear: true);
            Assert.That(inventory.TryGetSlotEntity(mob, "back", out var original), Is.EqualTo(originalBackpack));
            if (original is { } oldBag)
            {
                var pen = entities.SpawnEntity("Pen", map.GridCoords);
                Assert.That(entities.System<SharedStorageSystem>().Insert(oldBag, pen, out _, playSound: false), Is.True);
            }

            Assert.That(entities.System<PersonalLoadoutEquipSystem>().Apply(mob, profile, "Mercenary", null), Is.Empty);
            spawning.FinishDeferredStartingGear(mob);
            spawning.FinishDeferredStartingGear(mob);

            Assert.That(inventory.TryGetSlotEntity(mob, "back", out var backpack), Is.True);
            Assert.That(entities.GetComponent<MetaDataComponent>(backpack!.Value).EntityPrototype?.ID, Is.EqualTo("ClothingBackpackSatchelLeather"));
            var contents = entities.GetComponent<StorageComponent>(backpack.Value).Container.ContainedEntities
                .Select(item => entities.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID).ToArray();
            var expected = new[] { "ClothingMaskGasMercenary", "AppraisalCartridge", "WeaponPistolPollock" };
            Assert.That(contents, Is.EquivalentTo(originalBackpack ? expected.Append("Pen") : expected));

            Assert.That(inventory.TryGetSlotEntity(mob, "ears", out var headset), Is.True);
            var keys = entities.GetComponent<EncryptionKeyHolderComponent>(headset!.Value).KeyContainer.ContainedEntities
                .Select(item => entities.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID);
            Assert.That(keys.Count(id => id == "EncryptionKeyTraffic"), Is.EqualTo(1));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SavedLayoutIsAppliedInTheModsuitModule()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var inventory = entities.System<InventorySystem>();
            var spawning = entities.System<StationSpawningSystem>();
            var role = new RoleLoadout("JobMercenary");
            role.SelectedLoadouts["MercenaryBackpack"] = new() { new() { Prototype = "MercenaryClothingModsuitSecurityPowerCell" } };
            role.SelectedLoadouts["MercenaryFace"] = new() { new() { Prototype = "MercenaryClothingMaskGasMercenary" } };
            role.SelectedLoadouts["ContractorCartridge"] = new() { new() { Prototype = "ContractorAppraisalCartridge" } };
            role.SelectedLoadouts["MercenaryPistol"] = new() { new() { Prototype = "MercenaryPollockLoadout" } };
            role.BackpackLayout["loadout/ContractorAppraisalCartridge/0/AppraisalCartridge"] = new(true, 0, 0, 0);
            role.BackpackLayout["loadout/MercenaryClothingMaskGasMercenary/0/ClothingMaskGasMercenary"] = new(false, 4, 2, 90);
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithLoadout(role);
            var mob = spawning.SpawnPlayerMob(map.GridCoords, "Mercenary", profile, null, deferStartingGear: true);
            spawning.FinishDeferredStartingGear(mob);

            Assert.That(inventory.TryGetSlotEntity(mob, "back", out var suit), Is.True);
            Assert.That(entities.HasComponent<StorageComponent>(suit!.Value), Is.False);
            var backpack = spawning.GetBackpackStorage(mob);
            Assert.That(backpack, Is.Not.Null);
            var bag = backpack!.Value;
            Assert.That(entities.GetComponent<MetaDataComponent>(bag.Owner).EntityPrototype?.ID, Is.EqualTo("ModModuleStorage"));
            var contents = bag.Comp.StoredItems.ToDictionary(
                entry => entities.GetComponent<MetaDataComponent>(entry.Key).EntityPrototype!.ID, entry => entry.Value);
            Assert.That(contents.Keys, Is.EquivalentTo(new[] { "ClothingMaskGasMercenary", "WeaponPistolPollock" }));
            var mask = contents["ClothingMaskGasMercenary"];
            Assert.That(mask.Position, Is.EqualTo(new Robust.Shared.Maths.Vector2i(4, 2)));
            Assert.That(mask.Rotation.Degrees, Is.EqualTo(90).Within(0.01));
        });
        await pair.CleanReturnAsync();
    }
}

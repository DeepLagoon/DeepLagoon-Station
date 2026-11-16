using System.Collections.Generic;
using System.Linq;
using Content.Server.Preferences.Managers;
using Content.Server.Station.Systems;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.CCVar;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Radio.Components;
using Content.Shared.Station;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class LoadoutStorageSpawnTests
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task RoleStorageArrivesInThePersonalBag(bool originalBackpack, bool removeSurvivalKit)
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
            const string personalBag = "DLEELoadoutItemBackpackSatchelLeather";
            var personal = new RoleLoadout(PersonalLoadoutSystem.Role);
            var group = prototypes.Index<RoleLoadoutPrototype>(PersonalLoadoutSystem.Role).Groups
                .First(g => prototypes.Index(g).Loadouts.Any(item => item.Id == personalBag));
            personal.SelectedLoadouts[group] = new() { new() { Prototype = personalBag } };
            if (removeSurvivalKit)
                personal.BackpackLayout[SharedStationSpawningSystem.DefaultSurvivalKit] = new(true, 0, 0, 0);
            var player = pair.Player!;
            var profile = CacheProfile(pair.Server.ResolveDependency<IServerPreferencesManager>(), player,
                HumanoidCharacterProfile.DefaultWithSpecies("Human").WithLoadout(role).WithLoadout(personal));

            var mob = spawning.SpawnPlayerMob(map.GridCoords, "Mercenary", profile, null, session: player, deferStartingGear: true);
            if (inventory.TryGetSlotEntity(mob, "back", out var fallback))
            {
                Assert.That(inventory.TryUnequip(mob, "back", silent: true, force: true), Is.True);
                entities.DeleteEntity(fallback!.Value);
            }
            if (originalBackpack)
            {
                var oldBag = entities.SpawnEntity("ClothingBackpackMercenary", map.GridCoords);
                Assert.That(inventory.TryEquip(mob, oldBag, "back", silent: true, force: true), Is.True);
                var pen = entities.SpawnEntity("Pen", map.GridCoords);
                Assert.That(entities.System<SharedStorageSystem>().Insert(oldBag, pen, out _, playSound: false), Is.True);
            }

            Assert.That(entities.System<PersonalLoadoutEquipSystem>().Apply(mob, profile, "Mercenary", player), Is.Empty);
            spawning.FinishDeferredStartingGear(mob);
            spawning.FinishDeferredStartingGear(mob);

            Assert.That(inventory.TryGetSlotEntity(mob, "back", out var backpack), Is.True);
            Assert.That(entities.GetComponent<MetaDataComponent>(backpack!.Value).EntityPrototype?.ID, Is.EqualTo("ClothingBackpackSatchelLeather"));
            var contents = entities.GetComponent<StorageComponent>(backpack.Value).Container.ContainedEntities
                .Select(item => entities.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID).ToArray();
            var expected = new List<string> { "ClothingMaskGasMercenary", "AppraisalCartridge", "WeaponPistolPollock" };
            if (originalBackpack)
                expected.Add("Pen");
            if (!removeSurvivalKit)
                expected.Add("BoxSurvivalMonoOxygenExtended");
            Assert.That(contents, Is.SupersetOf(expected));
            Assert.That(contents.Count(id => id == "BoxSurvivalMonoOxygenExtended"), Is.EqualTo(removeSurvivalKit ? 0 : 1));
            Assert.That(contents.Contains("Pen"), Is.EqualTo(originalBackpack));

            Assert.That(inventory.TryGetSlotEntity(mob, "ears", out var headset), Is.True);
            var keys = entities.GetComponent<EncryptionKeyHolderComponent>(headset!.Value).KeyContainer.ContainedEntities
                .Select(item => entities.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID);
            Assert.That(keys.Count(id => id == "EncryptionKeyTraffic"), Is.EqualTo(1));
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SavedLayoutIsAppliedInTheModsuitModule(bool invalidPistolPosition)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var inventory = entities.System<InventorySystem>();
            var spawning = entities.System<StationSpawningSystem>();
            var role = new RoleLoadout("JobMercenary");
            role.SelectedLoadouts["MercenaryFace"] = new() { new() { Prototype = "MercenaryClothingMaskGasMercenary" } };
            role.SelectedLoadouts["ContractorCartridge"] = new() { new() { Prototype = "ContractorAppraisalCartridge" } };
            role.SelectedLoadouts["MercenaryPistol"] = new() { new() { Prototype = "MercenaryPollockLoadout" } };
            role.BackpackLayout["loadout/ContractorAppraisalCartridge/0/AppraisalCartridge"] = new(true, 0, 0, 0);
            role.BackpackLayout["loadout/MercenaryClothingMaskGasMercenary/0/ClothingMaskGasMercenary"] = new(false, 4, 2, 90);
            if (invalidPistolPosition)
                role.BackpackLayout["loadout/MercenaryPollockLoadout/0/WeaponPistolPollock"] = new(false, 40, 20, 180);
            var player = pair.Player!;
            var profile = CacheProfile(pair.Server.ResolveDependency<IServerPreferencesManager>(), player,
                HumanoidCharacterProfile.DefaultWithSpecies("Human").WithLoadout(role));
            var mob = spawning.SpawnPlayerMob(map.GridCoords, "Mercenary", profile, null, session: player, deferStartingGear: true);
            if (inventory.TryGetSlotEntity(mob, "back", out var initialBag))
            {
                Assert.That(inventory.TryUnequip(mob, "back", silent: true, force: true), Is.True);
                entities.DeleteEntity(initialBag!.Value);
            }
            var modsuit = entities.SpawnEntity("ClothingModsuitSecurityPowerCell", map.GridCoords);
            Assert.That(inventory.TryEquip(mob, modsuit, "back", silent: true, force: true), Is.True);
            spawning.FinishDeferredStartingGear(mob);

            Assert.That(inventory.TryGetSlotEntity(mob, "back", out var suit), Is.True);
            Assert.That(entities.HasComponent<StorageComponent>(suit!.Value), Is.False);
            var backpack = spawning.GetBackpackStorage(mob);
            Assert.That(backpack, Is.Not.Null);
            var bag = backpack!.Value;
            Assert.That(entities.GetComponent<MetaDataComponent>(bag.Owner).EntityPrototype?.ID, Is.EqualTo("ModModuleStorage"));
            var contents = bag.Comp.StoredItems.ToDictionary(
                entry => entities.GetComponent<MetaDataComponent>(entry.Key).EntityPrototype!.ID, entry => entry.Value);
            Assert.That(contents.Keys, Does.Contain("BoxSurvivalMonoOxygenExtended"));
            Assert.That(contents.ContainsKey("WeaponPistolPollock"), Is.EqualTo(!invalidPistolPosition));
            Assert.That(contents.Keys, Does.Not.Contain("AppraisalCartridge"));
            if (invalidPistolPosition)
            {
                var pistol = entities.EntityQuery<MetaDataComponent>().Single(meta => meta.EntityPrototype?.ID == "WeaponPistolPollock").Owner;
                Assert.That(entities.System<SharedTransformSystem>().GetMapCoordinates(pistol), Is.EqualTo(entities.System<SharedTransformSystem>().GetMapCoordinates(mob)));
                Assert.That(entities.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>().EnumerateHeld(mob), Does.Not.Contain(pistol));
            }
            var mask = contents["ClothingMaskGasMercenary"];
            Assert.That(mask.Position, Is.EqualTo(new Robust.Shared.Maths.Vector2i(4, 2)));
            Assert.That(mask.Rotation.Degrees, Is.EqualTo(90).Within(0.01));
        });
        await pair.CleanReturnAsync();
    }

    private static HumanoidCharacterProfile CacheProfile(IServerPreferencesManager preferences, ICommonSession player,
        HumanoidCharacterProfile profile)
    {
        var prefs = preferences.GetPreferences(player.UserId);
        preferences.SetProfile(player.UserId, prefs.SelectedCharacterIndex,
            profile.WithBankBalance(((HumanoidCharacterProfile)prefs.SelectedCharacter).BankBalance), persist: false).GetAwaiter().GetResult();
        return (HumanoidCharacterProfile)preferences.GetPreferences(player.UserId).SelectedCharacter;
    }
}

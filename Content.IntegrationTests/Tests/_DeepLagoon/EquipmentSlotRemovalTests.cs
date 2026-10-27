using System.Linq;
using System.Threading.Tasks;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Lobby;
using Content.Client.Lobby.UI.Loadouts;
using Content.Server.Station.Systems;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.CCVar;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using NUnit.Framework;
using Robust.Client.UserInterface;
using Robust.Shared.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class EquipmentSlotRemovalTests
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: startingGear
  id: SlotRemovalTesterBase
  equipment:
    jumpsuit: ClothingUniformJumpsuitColorGrey
    shoes: ClothingShoesColorBlack
    id: PassengerPDA
    back: ClothingBackpack
    ears: ClothingHeadsetGrey
- type: loadout
  id: SlotRemovalTesterGear
  startingGear: SlotRemovalTesterBase
- type: loadoutGroup
  id: SlotRemovalTesterMandatory
  name: generic-unknown
  hidden: true
  minLimit: 1
  maxLimit: 1
  loadouts: [SlotRemovalTesterGear]
- type: roleLoadout
  id: JobSlotRemovalTester
  groups: [SlotRemovalTesterMandatory]
- type: playTimeTracker
  id: PlayTimeSlotRemovalTester
- type: job
  id: SlotRemovalTester
  name: generic-unknown
  playTimeTracker: PlayTimeSlotRemovalTester
  startingGear: SlotRemovalTesterBase
";

    [Test]
    public async Task MandatoryHiddenAndBaseGearCanAllBeRemovedAndStayEmptyAtSpawn()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var requested = new[] { "shoes", "id", "back", "ears", "jumpsuit" };
        var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
        await pair.Client.WaitAssertion(() =>
        {
            pair.Client.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.PersonalLoadoutsEnabled, true);
            var entities = pair.Client.ResolveDependency<IEntityManager>();
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var lobby = pair.Client.ResolveDependency<IUserInterfaceManager>().GetUIController<LobbyUIController>();
            var job = prototypes.Index<JobPrototype>("SlotRemovalTester");
            var inventory = entities.System<InventorySystem>();
            using var editor = new PersonalLoadoutEditor { TguiMode = true };
            var dummy = lobby.LoadProfileEntity(profile, job, true);
            editor.ProfileChanged += changed => profile = changed;
            try
            {
                foreach (var slot in requested)
                {
                    Assert.That(inventory.TryGetSlotEntity(dummy, slot, out _), Is.True, slot);
                    editor.Refresh(profile, job.ID, null);
                    Assert.That(editor.RemoveTguiSlot(dummy, slot), Is.True, slot);
                    entities.DeleteEntity(dummy);
                    dummy = lobby.LoadProfileEntity(profile, job, true);
                    foreach (var removed in profile.Loadouts["JobSlotRemovalTester"].UnequippedSlots)
                        Assert.That(inventory.TryGetSlotEntity(dummy, removed, out _), Is.False, removed);
                }
                var role = profile.Loadouts["JobSlotRemovalTester"].Clone();
                role.EnsureValid(profile, null, Robust.Shared.IoC.IoCManager.Instance!);
                Assert.That(role.SelectedLoadouts["SlotRemovalTesterMandatory"], Has.Count.EqualTo(1));
                Assert.That(role.UnequippedSlots, Is.EquivalentTo(requested));
                Assert.That(editor.RemoveTguiSlot(dummy, "back"), Is.False, "An empty slot is a no-op");
                var system = entities.System<PersonalLoadoutSystem>();
                var restored = system.RestoreEquipmentSlots(profile, job.ID, prototypes.Index<LoadoutPrototype>("SlotRemovalTesterGear"));
                var dressed = lobby.LoadProfileEntity(restored, job, true);
                try
                {
                    foreach (var slot in requested)
                        Assert.That(inventory.TryGetSlotEntity(dressed, slot, out _), Is.True, slot);
                }
                finally { entities.DeleteEntity(dressed); }
                // Choosing personal clothing restores only its own slot, and
                // removing it again must not expose the job's underlying gear.
                editor.Refresh(profile, job.ID, null);
                _ = editor.CreateTguiState();
                Assert.That(TguiActionData.TryParse("{\"id\":\"DLEELoadoutClothingJumpsuitSuitBlack\"}", out var select), Is.True);
                Assert.That(editor.HandleTguiAction("select", select!), Is.True);
                entities.DeleteEntity(dummy);
                dummy = lobby.LoadProfileEntity(profile, job, true);
                Assert.That(inventory.TryGetSlotEntity(dummy, "jumpsuit", out _), Is.True);
                foreach (var slot in requested.Where(slot => slot != "jumpsuit"))
                    Assert.That(inventory.TryGetSlotEntity(dummy, slot, out _), Is.False, slot);
                editor.Refresh(profile, job.ID, null);
                Assert.That(editor.RemoveTguiSlot(dummy, "jumpsuit"), Is.True);
                entities.DeleteEntity(dummy);
                dummy = lobby.LoadProfileEntity(profile, job, true);
                Assert.That(inventory.TryGetSlotEntity(dummy, "jumpsuit", out _), Is.False);
            }
            finally { entities.DeleteEntity(dummy); }
        });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.ResolveDependency<IEntityManager>();
            var spawned = entities.System<StationSpawningSystem>().SpawnPlayerMob(map.GridCoords,
                "SlotRemovalTester", profile, null);
            try
            {
                var inventory = entities.System<InventorySystem>();
                foreach (var slot in requested)
                    Assert.That(inventory.TryGetSlotEntity(spawned, slot, out _), Is.False, slot);
            }
            finally { entities.DeleteEntity(spawned); }
        });
        await pair.CleanReturnAsync();
    }
}

using System.Linq;
using System.Threading.Tasks;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Lobby.UI.Loadouts;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.CCVar;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Content.Shared.Station;
using Content.Shared.Storage;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class GuitarLoadoutTests
{
    [Test]
    public async Task GuitarsKeepBackpackAndContentsInPreviewAndAtSpawn()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Server.WaitPost(() => pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.PersonalLoadoutsEnabled, true));
        await pair.RunTicksSync(2);
        foreach (var client in new[] { true, false })
        {
            void Check()
            {
                var entities = client ? pair.Client.EntMan : pair.Server.EntMan;
                var prototypes = client ? pair.Client.ResolveDependency<IPrototypeManager>() : pair.Server.ResolveDependency<IPrototypeManager>();
                var loadouts = entities.System<PersonalLoadoutSystem>();
                var inventory = entities.System<InventorySystem>();
                var hands = entities.System<SharedHandsSystem>();
                foreach (var guitar in new[] { "AcousticGuitar", "BassGuitar", "ElectricGuitar", "RockGuitar" })
                foreach (var musician in new[] { false, true })
                foreach (var oldSlot in new[] { false, true })
                {
                    var id = "DLEELoadoutItem" + guitar + (musician ? "InstrumentMusician" : "");
                    var prototype = prototypes.Index<LoadoutPrototype>(id);
                    var role = new RoleLoadout(PersonalLoadoutSystem.Role);
                    var group = prototypes.Index<RoleLoadoutPrototype>(PersonalLoadoutSystem.Role).Groups
                        .First(g => prototypes.Index(g).Loadouts.Any(l => l.Id == id));
                    role.SelectedLoadouts[group] = new() { new Loadout { Prototype = id,
                        Customization = new() { Slot = oldSlot ? "back" : null, Name = "My guitar" } } };
                    var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithLoadout(role);
                    Assert.That(loadouts.PersonalSlot(profile, prototype, "back"), Is.Null);
                    Assert.That(loadouts.Conflicts(profile, prototype,
                        new LoadoutPrototype { Equipment = new() { ["back"] = "ClothingBackpack" } }, "back"), Is.False);
                    var mob = entities.SpawnEntity(client ? "MobHumanDummy" : "MobHuman", MapCoordinates.Nullspace);
                    try
                    {
                        entities.System<SharedStationSpawningSystem>().EquipStartingGear(mob, new StartingGearPrototype
                        {
                            Equipment = new() { ["back"] = "ClothingBackpack" },
                            Storage = new() { ["back"] = new() { "Crowbar" } },
                            Inhand = client ? new() : new() { "Wrench" },
                        });
                        Assert.That(inventory.TryGetSlotEntity(mob, "back", out var backpack), Is.True);
                        var contents = entities.GetComponent<StorageComponent>(backpack.Value).Container.ContainedEntities.ToArray();
                        Assert.That(contents, Has.Length.EqualTo(1));
                        // Preview dolls have no functional hands; verify hand delivery on the server mob.
                        EntityUid? held = client ? null : hands.EnumerateHeld(mob).Single();
                        Assert.That(entities.System<PersonalLoadoutEquipSystem>().Apply(mob, profile,
                            musician ? "Musician" : "Passenger", null, preview: client), Is.Empty, id);
                        Assert.That(inventory.TryGetSlotEntity(mob, "back", out var after), Is.True, id);
                        Assert.That(after, Is.EqualTo(backpack), id);
                        Assert.That(entities.EntityExists(contents[0]), Is.True, id);
                        Assert.That(entities.GetComponent<StorageComponent>(backpack.Value).Container.ContainedEntities,
                            Is.EquivalentTo(contents), id);
                        if (client)
                            continue;
                        var items = hands.EnumerateHeld(mob).ToArray();
                        Assert.That(items, Has.Length.EqualTo(2), id);
                        Assert.That(items, Does.Contain(held.Value), id);
                        var item = items.Single(item => item != held);
                        Assert.That(entities.GetComponent<MetaDataComponent>(item).EntityPrototype!.ID,
                            Is.EqualTo(guitar + "Instrument"), id);
                        Assert.That(entities.GetComponent<MetaDataComponent>(item).EntityName, Is.EqualTo("My guitar"));
                    }
                    finally { entities.DeleteEntity(mob); }
                }
            }
            if (client) await pair.Client.WaitAssertion(Check);
            else await pair.Server.WaitAssertion(Check);
        }
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SelectingGuitarInEditorKeepsSelectedBackpack()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Server.WaitPost(() => pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.PersonalLoadoutsEnabled, true));
        await pair.RunTicksSync(2);
        await pair.Client.WaitAssertion(() =>
        {
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
            using var editor = new PersonalLoadoutEditor { TguiMode = true };
            editor.ProfileChanged += changed => profile = changed;
            foreach (var id in new[] { "DLEELoadoutItemBackpackSatchelLeather", "DLEELoadoutItemAcousticGuitar" })
            {
                editor.Refresh(profile, "Passenger", null);
                _ = editor.CreateTguiState();
                Assert.That(TguiActionData.TryParse("{\"id\":\"" + id + "\"}", out var args), Is.True);
                Assert.That(editor.HandleTguiAction("select", args!), Is.True);
            }
            var selections = pair.Client.EntMan.System<PersonalLoadoutSystem>().GetSelections(profile).ToArray();
            Assert.That(selections.Select(item => item.Prototype.Id), Does.Contain("DLEELoadoutItemBackpackSatchelLeather"));
            Assert.That(selections.Single(item => item.Prototype.Id == "DLEELoadoutItemAcousticGuitar").Customization?.Slot, Is.Null);
        });
        await pair.CleanReturnAsync();
    }
}

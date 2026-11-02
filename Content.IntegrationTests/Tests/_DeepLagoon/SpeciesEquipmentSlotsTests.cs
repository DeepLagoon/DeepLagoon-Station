#nullable enable
using System.Linq;
using System.Text.Json;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Lobby;
using Content.Client.Lobby.UI.Loadouts;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.CCVar;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Robust.Client.UserInterface;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class SpeciesEquipmentSlotsTests
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: startingGear
  id: SpeciesSlotTestGlasses
  equipment:
    eyes: ClothingEyesGlasses
";

    [Test]
    public async Task ClientAndServerRejectMissingSpeciesSlotsAndCleanSavedSelections()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        void Check(IEntityManager entities, IPrototypeManager prototypes, IConfigurationManager configuration)
        {
            configuration.SetCVar(CCVars.PersonalLoadoutsEnabled, true);
            var system = entities.System<PersonalLoadoutSystem>();
            var human = HumanoidCharacterProfile.DefaultWithSpecies("Human");
            var hydrakin = human.WithSpecies("Hydrakin");
            var glasses = prototypes.Index<LoadoutPrototype>("ContractorClothingEyesGlasses");
            Assert.That(system.HasCompatibleSlots(human, glasses), Is.True);
            Assert.That(system.HasCompatibleSlots(hydrakin, glasses), Is.False);
            var startingGear = new LoadoutPrototype { StartingGear = "SpeciesSlotTestGlasses" };
            Assert.That(system.HasCompatibleSlots(human, startingGear), Is.True);
            Assert.That(system.HasCompatibleSlots(hydrakin, startingGear), Is.False);
            var personal = new LoadoutPrototype { PersonalItems = new() { "ClothingEyesGlasses" } };
            Assert.That(system.CanUse(personal, human, "Contractor", null, out _), Is.True);
            Assert.That(system.CanUse(personal, hydrakin, "Contractor", null, out var reason), Is.False);
            Assert.That(reason, Is.Not.Empty);
            personal.PersonalInhand = true;
            Assert.That(system.CanUse(personal, hydrakin, "Contractor", null, out _), Is.True,
                "Carrying glasses in a hand does not require an eyes slot");
            Assert.That(system.EditableSlots(hydrakin).Any(slot => slot.Name == "eyes"), Is.False);

            var role = human.GetLoadoutOrDefault("JobContractor", null, human.Species, entities, prototypes).Clone();
            var group = prototypes.Index<RoleLoadoutPrototype>("JobContractor").Groups.First(id =>
                prototypes.Index(id).Loadouts.Contains(glasses.ID) || prototypes.Index(id).Subgroups.Any(subgroup =>
                    prototypes.Index(subgroup).Loadouts.Contains(glasses.ID)));
            role.SelectedLoadouts[group] = new() { new Loadout { Prototype = glasses.ID } };
            Assert.That(role.IsValid(human, null, glasses.ID, IoCManager.Instance!, out _), Is.True);
            Assert.That(role.IsValid(hydrakin, null, glasses.ID, IoCManager.Instance!, out _), Is.False);
            role.EnsureValid(hydrakin, null, IoCManager.Instance!);
            Assert.That(role.SelectedLoadouts.Values.SelectMany(items => items).Any(item => item.Prototype.Id == glasses.ID), Is.False);
            Assert.That(role.SelectedLoadouts.Values.SelectMany(items => items).All(item =>
                system.HasCompatibleSlots(hydrakin, prototypes.Index(item.Prototype))), Is.True,
                "Validation must not restore an incompatible default");
        }
        await pair.Client.WaitAssertion(() => Check(pair.Client.EntMan,
            pair.Client.ResolveDependency<IPrototypeManager>(), pair.Client.ResolveDependency<IConfigurationManager>()));
        await pair.Server.WaitAssertion(() => Check(pair.Server.EntMan,
            pair.Server.ResolveDependency<IPrototypeManager>(), pair.Server.ResolveDependency<IConfigurationManager>()));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RealEquipmentEditorRejectsEyesSlotAndBothCataloguesAfterSpeciesChange()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            pair.Client.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.PersonalLoadoutsEnabled, true);
            var entities = pair.Client.EntMan;
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var lobby = pair.Client.ResolveDependency<IUserInterfaceManager>().GetUIController<LobbyUIController>();
            var system = entities.System<PersonalLoadoutSystem>();
            var inventory = entities.System<InventorySystem>();
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
            var job = prototypes.Index<JobPrototype>("Contractor");
            using var editor = new PersonalLoadoutEditor { TguiMode = true };
            editor.ProfileChanged += changed => profile = changed;
            editor.Refresh(profile, job.ID, null);
            using var humanState = JsonDocument.Parse(editor.CreateTguiState().ToString());
            var jobGlasses = humanState.RootElement.GetProperty("jobItems").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == "ContractorClothingEyesGlasses");
            Assert.That(jobGlasses.GetProperty("canSelect").GetBoolean(), Is.True);
            var personalGlasses = humanState.RootElement.GetProperty("items").EnumerateArray().First(item =>
                item.GetProperty("canSelect").GetBoolean() && !item.GetProperty("selected").GetBoolean() &&
                !system.HasCompatibleSlots(profile.WithSpecies("Hydrakin"), prototypes.Index<LoadoutPrototype>(item.GetProperty("id").GetString()!)));
            var personalId = personalGlasses.GetProperty("id").GetString()!;
            Assert.That(TguiActionData.TryParse(JsonSerializer.Serialize(new { id = personalId }), out var select), Is.True);
            Assert.That(editor.HandleTguiAction("select", select!), Is.True);
            Assert.That(system.GetSelections(profile).Any(item => item.Prototype.Id == personalId), Is.True);

            var humanDummy = lobby.LoadProfileEntity(profile, job, true);
            try { Assert.That(inventory.TryGetSlotEntity(humanDummy, "eyes", out _), Is.True); }
            finally { entities.DeleteEntity(humanDummy); }

            // Retain the human's saved selection when changing species, as an existing profile would.
            profile = profile.WithSpecies("Hydrakin");
            editor.Refresh(profile, job.ID, null);
            using var hydrakinState = JsonDocument.Parse(editor.CreateTguiState().ToString());
            var blockedPersonal = hydrakinState.RootElement.GetProperty("items").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == personalId);
            Assert.That(blockedPersonal.GetProperty("canSelect").GetBoolean(), Is.False);
            Assert.That(blockedPersonal.GetProperty("selected").GetBoolean(), Is.False);
            Assert.That(editor.HandleTguiAction("select", select!), Is.False);
            var blockedJob = hydrakinState.RootElement.GetProperty("jobItems").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == "ContractorClothingEyesGlasses");
            Assert.That(blockedJob.GetProperty("canSelect").GetBoolean(), Is.False);
            Assert.That(TguiActionData.TryParse(JsonSerializer.Serialize(new
            {
                id = "ContractorClothingEyesGlasses",
                group = jobGlasses.GetProperty("group").GetString(),
            }), out var jobSelect), Is.True);
            Assert.That(editor.HandleTguiAction("job-select", jobSelect!), Is.False);

            var dummy = lobby.LoadProfileEntity(profile, job, true);
            try
            {
                Assert.That(entities.GetComponent<InventoryComponent>(dummy).TemplateId, Is.EqualTo("hydrakin"));
                Assert.That(inventory.TryGetSlots(dummy, out var slots), Is.True);
                Assert.That(slots!.Any(slot => slot.Name == "eyes"), Is.False);
                Assert.That(editor.SelectTguiSlot(dummy, "eyes"), Is.False);
                Assert.That(inventory.TryGetSlotEntity(dummy, "eyes", out _), Is.False);
            }
            finally { entities.DeleteEntity(dummy); }
        });
        await pair.CleanReturnAsync();
    }
}

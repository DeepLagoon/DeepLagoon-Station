using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Lobby.UI.Loadouts;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.CCVar;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class CharacterEditorPreferencesTests
{
    [Test]
    public async Task EquipmentPricesAndUniformBudgetInCharacterEditor()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var editor = new PersonalLoadoutEditor { TguiMode = true };
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
            void Refresh(HumanoidCharacterProfile draft)
            {
                profile = draft;
                editor.Refresh(draft, "Contractor", null);
            }

            JsonElement State()
            {
                using var document = JsonDocument.Parse(editor.CreateTguiState().ToString());
                return document.RootElement.Clone();
            }

            JsonElement Item(string id) => State().GetProperty("jobItems").EnumerateArray()
                .First(item => item.GetProperty("id").GetString() == id);

            bool Select(string id)
            {
                var group = Item(id).GetProperty("group").GetString();
                Assert.That(TguiActionData.TryParse(JsonSerializer.Serialize(new { id, group }), out var action), Is.True);
                return editor.HandleTguiAction("job-select", action!);
            }

            try
            {
                editor.ProfileChanged += Refresh;
                Refresh(profile);
                const string uniform = "MercenaryClothingUniformJumpsuitPrivateSec";
                const string otherUniform = "MercenaryClothingUniformJumpskirtPrivateSec";
                const string glasses = "MercenaryClothingEyesGlassesMercenary";
                Assert.Multiple(() =>
                {
                    Assert.That(Item("ContractorClothingHeadHatGreysoft").GetProperty("cost").GetInt32(), Is.EqualTo(250));
                    Assert.That(Item("ContractorClothingOuterHardsuitEVA").GetProperty("cost").GetInt32(), Is.EqualTo(3500));
                    Assert.That(Item("ContractorMk32Loadout").GetProperty("cost").GetInt32(), Is.EqualTo(5000));
                    Assert.That(Item("ContractorMk32Loadout").GetProperty("selected").GetBoolean(), Is.False);
                    Assert.That(Item("ContractorClothingModsuitEngineerPowerCell").GetProperty("cost").GetInt32(), Is.EqualTo(10000));
                    Assert.That(Item("MercenaryClothingUniformJumpsuitMercenary").GetProperty("cost").GetInt32(), Is.Zero);
                    Assert.That(Item("MercenaryClothingUniformJumpsuitMercenary").GetProperty("selected").GetBoolean(), Is.True);
                    Assert.That(Item(uniform).GetProperty("costInPoints").GetBoolean(), Is.True);
                    Assert.That(Item(uniform).GetProperty("cost").GetInt32(), Is.EqualTo(1));
                });

                var originalPersonalPoints = State().GetProperty("points").GetInt32();
                Assert.That(Select(uniform), Is.True);
                Assert.That(Select("MercenaryClothingHeadBandMercenary"), Is.True);
                Assert.That(Select("MercenaryClothingHandsGlovesMercFingerless"), Is.True);
                Assert.That(State().GetProperty("jobPoints").GetInt32(), Is.Zero);
                Assert.That(Select(glasses), Is.False);
                Assert.That(Select(otherUniform), Is.True, "Replacing a choice releases its point");
                Assert.That(State().GetProperty("jobPoints").GetInt32(), Is.Zero);
                Assert.That(State().GetProperty("points").GetInt32(), Is.EqualTo(originalPersonalPoints));
                Assert.That(profile.Loadouts["JobContractor"].SelectedLoadouts["MercenaryJumpsuit"].Single().Prototype.Id, Is.EqualTo(otherUniform));

                editor.Refresh(profile, "CrocusDockworker", null);
                Assert.That(State().GetProperty("jobItems").EnumerateArray()
                    .Any(item => item.GetProperty("id").GetString() == otherUniform), Is.False);
            }
            finally
            {
                editor.Dispose();
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EditorActionsReplaceBothCurrenciesAtomically()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            pair.Client.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.PersonalLoadoutsEnabled, true);
            var entities = pair.Client.ResolveDependency<IEntityManager>();
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var system = entities.System<PersonalLoadoutSystem>();
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
            var editor = new PersonalLoadoutEditor { TguiMode = true };
            try
            {
                editor.ProfileChanged += changed =>
                {
                    profile = changed;
                    editor.Refresh(profile, "Contractor", null);
                };
                editor.Refresh(profile, "Contractor", null);
                using var state = JsonDocument.Parse(editor.CreateTguiState().ToString());
                var paid = state.RootElement.GetProperty("jobItems").EnumerateArray()
                    .First(item => item.GetProperty("category").GetString() == "Uniform" && item.GetProperty("canSelect").GetBoolean());
                var paidId = paid.GetProperty("id").GetString();
                var groupId = paid.GetProperty("group").GetString();
                const string personalId = "DLEELoadoutClothingJumpsuitSuitBlack";
                Assert.That(TguiActionData.TryParse(JsonSerializer.Serialize(new { id = personalId }), out var pointsAction), Is.True);
                Assert.That(TguiActionData.TryParse(JsonSerializer.Serialize(new { id = personalId, value = "#123456" }), out var paint), Is.True);
                Assert.That(editor.HandleTguiAction("paint", paint!), Is.True);
                Assert.That(TguiActionData.TryParse(JsonSerializer.Serialize(new { id = personalId, name = "Моя униформа", description = "Русское описание" }), out var rename), Is.True);
                Assert.That(editor.HandleTguiAction("rename", rename!), Is.True);
                Assert.That(system.GetSelections(profile), Is.Empty, "Customization must not select the item");
                Assert.That(editor.HandleTguiAction("select", pointsAction!), Is.True);
                Assert.That(system.GetSelections(profile).Any(item => item.Prototype.Id == personalId), Is.True);
                var personal = prototypes.Index<LoadoutPrototype>(personalId);
                Assert.That(profile.Loadouts["JobContractor"].SelectedLoadouts.Values.SelectMany(items => items)
                    .Any(item => system.Conflicts(profile, personal, prototypes.Index(item.Prototype))), Is.False);

                // Refresh the catalogue as the real editor does before the next action.
                _ = editor.CreateTguiState();
                Assert.That(TguiActionData.TryParse(JsonSerializer.Serialize(new { id = paidId, group = groupId }), out var moneyAction), Is.True);
                Assert.That(editor.HandleTguiAction("job-select", moneyAction!), Is.True);
                Assert.That(system.GetSelections(profile), Is.Empty);
                Assert.That(profile.Loadouts[PersonalLoadoutSystem.Role].Customizations[personalId].Name, Is.EqualTo("Моя униформа"));
                Assert.That(profile.Loadouts["JobContractor"].SelectedLoadouts[groupId!].Any(item => item.Prototype.Id == paidId), Is.True);

                using (var selectedState = JsonDocument.Parse(editor.CreateTguiState().ToString()))
                {
                    var other = selectedState.RootElement.GetProperty("jobItems").EnumerateArray()
                        .First(item => item.GetProperty("category").GetString() == "Uniform" && item.GetProperty("id").GetString() != paidId);
                    var otherId = other.GetProperty("id").GetString();
                    var otherGroup = other.GetProperty("group").GetString();
                    Assert.That(TguiActionData.TryParse(JsonSerializer.Serialize(new { id = otherId, group = otherGroup, jobItem = true, value = "#cc1122" }), out var paintOther), Is.True);
                    Assert.That(editor.HandleTguiAction("paint", paintOther!), Is.True);
                    var paidRole = profile.Loadouts["JobContractor"];
                    Assert.That(paidRole.Customizations[otherId!].Color, Is.EqualTo("#CC1122FF").IgnoreCase);
                    Assert.That(paidRole.SelectedLoadouts[groupId!].Single(item => item.Prototype.Id == paidId).Customization?.Color, Is.Null,
                        "Painting unselected clothing must not alter the worn clothing");
                    Assert.That(paidRole.SelectedLoadouts.Values.SelectMany(items => items).Any(item => item.Prototype.Id == otherId), Is.False);
                }
                _ = editor.CreateTguiState();
                Assert.That(editor.HandleTguiAction("select", pointsAction!), Is.True);
                Assert.That(system.GetSelections(profile).Any(item => item.Prototype.Id == personalId), Is.True);
                Assert.That(system.GetSelections(profile).Single(item => item.Prototype.Id == personalId).Customization!.Color, Is.EqualTo("#123456FF"));
                Assert.That(profile.Loadouts["JobContractor"].SelectedLoadouts.Values.SelectMany(items => items)
                    .Any(item => system.Conflicts(profile, personal, prototypes.Index(item.Prototype))), Is.False);
            }
            finally { editor.Dispose(); }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CyrillicNameAndDescriptionsSurviveProfileValidation()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Server.WaitAssertion(() =>
        {
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(CCVars.RestrictedNames, true);
            config.SetCVar(CCVars.ICNameCase, true);
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human")
                .WithName("Семён Ёлкин").WithFlavorText("Русское описание").WithOocNotes("Русские заметки");
            profile.EnsureValid(null!, IoCManager.Instance!);
            Assert.Multiple(() =>
            {
                Assert.That(profile.Name, Is.EqualTo("Семён Ёлкин"));
                Assert.That(profile.FlavorText, Is.EqualTo("Русское описание"));
                Assert.That(profile.OocNotes, Is.EqualTo("Русские заметки"));
            });
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ClothingReplacesOtherCurrencyAndDefaultsDoNotRestoreIt()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Server.WaitAssertion(() =>
        {
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(CCVars.PersonalLoadoutsEnabled, true);
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var system = pair.Server.ResolveDependency<IEntityManager>().System<PersonalLoadoutSystem>();
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
            const string personalId = "DLEELoadoutClothingJumpsuitSuitBlack";
            var personalItem = prototypes.Index<LoadoutPrototype>(personalId);
            var personalGroup = prototypes.Index<RoleLoadoutPrototype>(PersonalLoadoutSystem.Role).Groups
                .First(group => prototypes.Index(group).Loadouts.Any(item => item.Id == personalId));
            var personal = new RoleLoadout(PersonalLoadoutSystem.Role);
            personal.SelectedLoadouts[personalGroup] = new() { new() { Prototype = personalId } };
            var job = new RoleLoadout("JobContractor");
            job.SetDefault(profile, null, prototypes);
            job.EnsureValid(profile, null, IoCManager.Instance!);
            var uniformGroup = job.SelectedLoadouts.First(group => group.Value.Any(item => system.Conflicts(profile, personalItem, prototypes.Index(item.Prototype))));
            var uniform = uniformGroup.Value.First(item => system.Conflicts(profile, personalItem, prototypes.Index(item.Prototype)));
            var paidItem = prototypes.Index(uniform.Prototype);
            var unrelated = job.SelectedLoadouts.Values.SelectMany(items => items)
                .Where(item => !system.Conflicts(profile, personalItem, prototypes.Index(item.Prototype))).Select(item => item.Prototype.Id).ToArray();

            // Points replace money, including mandatory defaults after validation.
            profile = profile.WithLoadout(personal);
            job = system.RemoveJobConflicts(profile, job, personalItem);
            job.EnsureValid(profile, null, IoCManager.Instance!);
            Assert.That(job.SelectedLoadouts.Values.SelectMany(items => items).Any(item => system.Conflicts(profile, personalItem, prototypes.Index(item.Prototype))), Is.False);
            Assert.That(job.SelectedLoadouts.Values.SelectMany(items => items).Select(item => item.Prototype.Id), Does.Contain(unrelated.First()));

            // Money replaces points, restoring the chosen job item.
            profile = system.RemovePersonalConflicts(profile, paidItem);
            Assert.That(system.GetSelections(profile), Is.Empty);
            Assert.That(job.IsValid(profile, null, uniform.Prototype, IoCManager.Instance!, out _), Is.True);
            job.AddLoadout(uniformGroup.Key, uniform.Prototype, prototypes);
            job.EnsureValid(profile, null, IoCManager.Instance!);
            profile = profile.WithLoadout(job);
            profile.EnsureValid(null!, IoCManager.Instance!);
            Assert.That(profile.Loadouts[job.Role.Id].SelectedLoadouts[uniformGroup.Key].Any(item => item.Prototype == uniform.Prototype), Is.True);
            Assert.That(system.GetSelections(profile), Is.Empty);
        });
        await pair.CleanReturnAsync();
    }
}

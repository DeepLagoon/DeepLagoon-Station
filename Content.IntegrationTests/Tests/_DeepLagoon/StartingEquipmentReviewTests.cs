#nullable enable
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Lobby.UI;
using Content.Server.GameTicking;
using Content.Server.Players.PlayTimeTracking;
using Content.Server.Preferences.Managers;
using Content.Server.Station.Systems;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Content.Shared.Station;
using Content.Shared.Storage;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class StartingEquipmentReviewTests
{
    [TestPrototypes]
    private const string StationPrototype = """
        - type: entity
          id: TestEquipmentReviewStation
          components:
          - type: StationData
          - type: StationSpawning
          - type: StationJobs
            availableJobs:
              Mercenary: [1, 1]
        """;

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true, TestName = "ConfirmationCarriesCompatibleBackpackItems")]
    public async Task ConfirmationJoinsTheSelectedJobWithOrWithoutEquipmentChanges(bool edit, bool transfer)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var player = pair.Player!;
        var ticker = pair.Server.System<GameTicker>();
        const string role = "Mercenary";
        const string carried = "loadout/ContractorCigPackBlue/0/CigPackBlue";
        const string repacked = "loadout/ContractorCigPackRed/0/CigPackRed";
        const string excluded = "loadout/ContractorDoubleBarreledLoadout/0/WeaponShotgunDoubleBarreledRubber";
        const string originalAmmo = "loadout/Contractor9x19mmPistolAmmoLoadout/0/BoxMagazine9x19mmFMJ";
        const string transferredAmmo = "loadout/Mercenary9x19mmPistolAmmoLoadout/0/BoxMagazine9x19mmFMJ";
        const string magazine = "/contents/Magazine9x19mmPistolFMJ/0";
        const string boxContents = transferredAmmo + magazine;
        var roleId = new ProtoId<JobPrototype>(role);
        EntityUid station = default;
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.GameRoleTimers, true);
            var playtime = pair.Server.System<PlayTimeTrackingSystem>();
            pair.Server.ResolveDependency<PlayTimeTrackingManager>().AddTimeToOverallPlaytime(player, TimeSpan.FromHours(20));
            Assert.That(playtime.GetDisallowedJobs(player), Does.Not.Contain(roleId));
            if (transfer)
            {
                var preferences = pair.Server.ResolveDependency<IServerPreferencesManager>();
                var prefs = preferences.GetPreferences(player.UserId);
                var source = new RoleLoadout("JobContractor");
                source.SelectedLoadouts["ContractorTrinkets"] = [new() { Prototype = "ContractorCigPackBlue" }, new() { Prototype = "ContractorCigPackRed" }];
                source.SelectedLoadouts["ContractorMag"] = [new() { Prototype = "Contractor9x19mmPistolAmmoLoadout" }];
                source.SelectedLoadouts["ContractorFirearm"] = [new() { Prototype = "ContractorDoubleBarreledLoadout" }];
                source.BackpackLayout[carried] = new(false, 4, 3, 90);
                source.BackpackLayout[repacked] = new(false, 40, 2, 180);
                source.BackpackLayout[excluded] = new(false, 2, 2, 0);
                source.BackpackLayout[originalAmmo] = new(false, 4, 0, 0);
                source.BackpackLayout[originalAmmo + magazine] = new(false, 3, 0, 180);
                var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithLoadout(source)
                    .WithBankBalance(((HumanoidCharacterProfile) prefs.SelectedCharacter).BankBalance);
                preferences.SetProfile(player.UserId, prefs.SelectedCharacterIndex, profile, persist: false).GetAwaiter().GetResult();
                var saved = ticker.GetPlayerProfile(player);
                Assert.That(SharedGameTicker.PreferredEquipmentJob(saved), Is.EqualTo("Contractor"));
                Assert.That(saved.Loadouts["JobContractor"].SelectedLoadouts["ContractorFirearm"]
                    .Select(item => item.Prototype.Id), Does.Contain("ContractorDoubleBarreledLoadout"));
                preferences.SendPreferences(player);
            }
            Assert.That(SharedGameTicker.PreferredEquipmentJob(ticker.GetPlayerProfile(player)), Is.Not.EqualTo(role));
            if (ticker.RunLevel == GameRunLevel.PreRoundLobby)
            {
                ticker.SetGamePreset("sandbox");
                ticker.StartRound(force: true);
            }
            Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));

            var entities = pair.Server.EntMan;
            station = entities.SpawnEntity("TestEquipmentReviewStation", MapCoordinates.Nullspace);
            entities.System<StationSystem>().AddGridToStation(station, map.Grid);
            Assert.That(entities.System<StationJobsSystem>().TryAdjustJobSlot(station, role, 1, createSlot: true), Is.True);
            entities.SpawnEntity("SpawnPointLatejoin", map.GridCoords);

            ticker.MakeJoinGame(player, station, role);
            Assert.That(ticker.PlayerGameStatuses[player.UserId], Is.Not.EqualTo(PlayerGameStatus.JoinedGame));
            Assert.That(player.AttachedEntity, Is.Null);
        });
        await pair.RunTicksSync(5);

        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var window = ui.WindowRoot.Children.OfType<StartingEquipmentReviewWindow>().Single();
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var panel = (TguiPanel)typeof(StartingEquipmentReviewWindow).GetField("_panel", fields)!.GetValue(window)!;
            var state = (string)typeof(TguiPanel).GetField("_data", fields)!.GetValue(panel)!;
            using var document = JsonDocument.Parse(state);
            var backpack = document.RootElement.GetProperty("backpack");
            Assert.That(backpack.GetProperty("grid").GetArrayLength(), Is.GreaterThan(0));
            var kit = backpack.GetProperty("items").EnumerateArray().Single(item =>
                item.GetProperty("iconId").GetString() == "item/BoxSurvivalMonoOxygenExtended");
            if (transfer)
            {
                var items = backpack.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("key").GetString()!);
                Assert.That(items.Keys, Does.Not.Contain(excluded));
                Assert.That(items[carried].GetProperty("x").GetInt32(), Is.EqualTo(4));
                Assert.That(items[carried].GetProperty("y").GetInt32(), Is.EqualTo(3));
                Assert.That(items[carried].GetProperty("rotation").GetInt32(), Is.EqualTo(90));
                Assert.That(items[repacked].GetProperty("overflow").GetBoolean(), Is.False);
                Assert.That(items[transferredAmmo].GetProperty("x").GetInt32(), Is.EqualTo(4));
                Assert.That(items[boxContents].GetProperty("rotation").GetInt32(), Is.EqualTo(180));
            }
            if (edit)
            {
                typeof(StartingEquipmentReviewWindow).GetMethod("OnAction", fields)!.Invoke(window,
                    ["backpack-remove", JsonSerializer.Serialize(new { key = kit.GetProperty("key").GetString() })]);
            }

            var join = window.Contents.Children.Single().Children.OfType<Button>().Single();
            var press = (Action<BaseButton.ButtonEventArgs>) typeof(BaseButton).GetField(nameof(BaseButton.OnPressed), fields)!.GetValue(join)!;
            press(new BaseButton.ButtonEventArgs(join, null!));
        });
        await pair.RunTicksSync(10);

        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(ticker.PlayerGameStatuses[player.UserId], Is.EqualTo(PlayerGameStatus.JoinedGame));
            Assert.That(pair.Server.System<StationJobsSystem>().TryGetPlayerJobs(station, player.UserId, out var jobs), Is.True);
            Assert.That(jobs, Is.EquivalentTo(new[] { roleId }));
            Assert.That(SharedStationSpawningSystem.SurvivalKitRemoved(ticker.GetPlayerProfile(player)), Is.EqualTo(edit));
            var bag = pair.Server.System<StationSpawningSystem>().GetBackpackStorage(player.AttachedEntity!.Value);
            Assert.That(bag, Is.Not.Null);
            Assert.That(bag!.Value.Comp.StoredItems.Keys.Any(item =>
                pair.Server.EntMan.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID == "BoxSurvivalMonoOxygenExtended"), Is.EqualTo(!edit));
            if (transfer)
            {
                var profile = ticker.GetPlayerProfile(player);
                var layout = profile.Loadouts["JobMercenary"].BackpackLayout;
                Assert.That(layout.Keys, Is.EquivalentTo(new[] { carried, boxContents, transferredAmmo }));
                Assert.That(profile.Loadouts["JobContractor"].BackpackLayout.Keys, Does.Contain(excluded));
                var items = bag.Value.Comp.StoredItems.ToLookup(item =>
                    pair.Server.EntMan.GetComponent<MetaDataComponent>(item.Key).EntityPrototype!.ID, item => item.Value);
                Assert.That(items.Contains("WeaponShotgunDoubleBarreledRubber"), Is.False);
                Assert.That(items["BoxMagazine9x19mmFMJ"].Single().Position, Is.EqualTo(new Vector2i(4, 0)));
                Assert.That(items["CigPackRed"].Count(), Is.EqualTo(1));
                Assert.That(items["CigPackBlue"].Single().Position, Is.EqualTo(new Vector2i(4, 3)));
                Assert.That(items["CigPackBlue"].Single().Rotation.Degrees, Is.EqualTo(90).Within(0.001));
                Assert.That(items["Magazine9x19mmPistolFMJ"].Single().Position, Is.EqualTo(new Vector2i(3, 0)));
            }
        });
        await pair.CleanReturnAsync();
    }
}

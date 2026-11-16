#nullable enable
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Lobby.UI;
using Content.Server.GameTicking;
using Content.Server.Station.Systems;
using Content.Shared.GameTicking;
using Content.Shared.Preferences;
using Content.Shared.Station;
using Content.Shared.Storage;
using Robust.Client.UserInterface;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

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

    [TestCase(false)]
    [TestCase(true)]
    public async Task ChangedJobWaitsForApprovalAndSavesOnlyAppliedEdits(bool apply)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var player = pair.Player!;
        var ticker = pair.Server.System<GameTicker>();
        const string role = "Mercenary";
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(SharedGameTicker.PreferredEquipmentJob(ticker.GetPlayerProfile(player)), Is.Not.EqualTo(role));
            if (ticker.RunLevel == GameRunLevel.PreRoundLobby)
            {
                ticker.SetGamePreset("sandbox");
                ticker.StartRound(force: true);
            }
            Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));

            var entities = pair.Server.EntMan;
            var station = entities.SpawnEntity("TestEquipmentReviewStation", MapCoordinates.Nullspace);
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
            typeof(StartingEquipmentReviewWindow).GetMethod("OnAction", fields)!.Invoke(window,
                ["backpack-remove", JsonSerializer.Serialize(new { key = kit.GetProperty("key").GetString() })]);
            typeof(StartingEquipmentReviewWindow).GetMethod("Finish", fields)!.Invoke(window, [apply]);
        });
        await pair.RunTicksSync(10);

        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(ticker.PlayerGameStatuses[player.UserId], Is.EqualTo(PlayerGameStatus.JoinedGame));
            Assert.That(SharedStationSpawningSystem.SurvivalKitRemoved(ticker.GetPlayerProfile(player)), Is.EqualTo(apply));
            var bag = pair.Server.System<StationSpawningSystem>().GetBackpackStorage(player.AttachedEntity!.Value);
            Assert.That(bag, Is.Not.Null);
            Assert.That(bag!.Value.Comp.StoredItems.Keys.Any(item =>
                pair.Server.EntMan.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID == "BoxSurvivalMonoOxygenExtended"), Is.EqualTo(!apply));
        });
        await pair.CleanReturnAsync();
    }
}

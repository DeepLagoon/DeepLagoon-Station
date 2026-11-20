using System;
using System.IO;
using System.Linq;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server.Salvage;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Systems;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Shuttles.Components;
using Content.Shared.Station.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ShuttleParkingExpeditionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ExplicitGridExpeditionPolicySurvivesParkingAndRepeatedRestore(bool enabled)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var gridMetadata = entities.GetComponent<MetaDataComponent>(map.Grid);
            entities.RunMapInit(map.Grid, gridMetadata);
            Assert.That(gridMetadata.EntityLifeStage, Is.EqualTo(EntityLifeStage.MapInitialized));
            var salvage = entities.System<SalvageSystem>();
            var parking = entities.System<ParkingSystem>();
            entities.EnsureComponent<ShuttleComponent>(map.Grid);
            entities.SpawnEntity("ComputerSalvageExpedition", map.GridCoords);
            var source = entities.AddComponent<SalvageExpeditionDataComponent>(map.Grid);
            source.Enabled = enabled;
            source.NextOffer = pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromMinutes(5);
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            var restored = parking.LoadSnapshot(parking.SerializeSnapshot(map.Grid));
            Assert.That(salvage.GridExpeditionsEnabled(restored), Is.EqualTo(enabled));
            Assert.That(entities.GetComponent<ParkingShuttleComponent>(restored).ExpeditionsEnabled, Is.EqualTo(enabled));
            var data = entities.GetComponent<SalvageExpeditionDataComponent>(restored);
            Assert.That(data.Enabled, Is.EqualTo(enabled));
            Assert.That(data.NextOffer, Is.EqualTo(source.NextOffer));
            AssertConsoleState(entities, restored, enabled, cooldown: !enabled);
            AssertIdempotent(entities, restored, enabled);
            entities.DeleteEntity(restored);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StationOnlyExpeditionPolicyAndCooldownSurviveOutsideStationSnapshot(bool enabled)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var gridMetadata = entities.GetComponent<MetaDataComponent>(map.Grid);
            entities.RunMapInit(map.Grid, gridMetadata);
            Assert.That(gridMetadata.EntityLifeStage, Is.EqualTo(EntityLifeStage.MapInitialized));
            var salvage = entities.System<SalvageSystem>();
            var parking = entities.System<ParkingSystem>();
            entities.EnsureComponent<ShuttleComponent>(map.Grid);
            var station = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            entities.AddComponent<StationDataComponent>(station);
            entities.System<StationSystem>().AddGridToStation(station, map.Grid);
            var source = entities.AddComponent<SalvageExpeditionDataComponent>(station);
            source.Enabled = enabled;
            source.Cooldown = true;
            source.NextOffer = pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromMinutes(5);
            entities.SpawnEntity("ComputerSalvageExpedition", map.GridCoords);
            Assert.That(entities.HasComponent<SalvageExpeditionDataComponent>(map.Grid), Is.False);
            Assert.That(salvage.GridExpeditionsEnabled(map.Grid), Is.EqualTo(enabled));
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            var restored = parking.LoadSnapshot(parking.SerializeSnapshot(map.Grid));
            Assert.That(entities.HasComponent<StationMemberComponent>(restored), Is.False,
                "The owning station deliberately does not travel inside the grid snapshot.");
            Assert.That(salvage.GridExpeditionsEnabled(restored), Is.EqualTo(enabled));
            var data = entities.GetComponent<SalvageExpeditionDataComponent>(restored);
            Assert.That(data.Enabled, Is.EqualTo(enabled));
            Assert.That(data.Cooldown, Is.True, "Parking must not bypass an expedition cooldown owned by the former station.");
            Assert.That(data.NextOffer, Is.EqualTo(source.NextOffer));
            AssertConsoleState(entities, restored, enabled, cooldown: true);
            AssertIdempotent(entities, restored, enabled);
            entities.DeleteEntity(restored);
            entities.DeleteEntity(station);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConsoleAloneNeverGrantsExpeditionCapability(bool legacy)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var gridMetadata = entities.GetComponent<MetaDataComponent>(map.Grid);
            entities.RunMapInit(map.Grid, gridMetadata);
            Assert.That(gridMetadata.EntityLifeStage, Is.EqualTo(EntityLifeStage.MapInitialized));
            var parking = entities.System<ParkingSystem>();
            var salvage = entities.System<SalvageSystem>();
            entities.EnsureComponent<ShuttleComponent>(map.Grid);
            entities.SpawnEntity("ComputerSalvageExpedition", map.GridCoords);
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            var snapshot = parking.SerializeSnapshot(map.Grid);
            var restored = parking.LoadSnapshot(legacy ? WithoutExpeditionMetadata(snapshot) : snapshot);
            Assert.That(salvage.GridExpeditionsEnabled(restored), Is.False);
            if (legacy)
            {
                Assert.That(entities.GetComponent<ParkingShuttleComponent>(restored).ExpeditionsEnabled, Is.Null);
                Assert.That(entities.HasComponent<SalvageExpeditionDataComponent>(restored), Is.False);
            }
            else
                Assert.That(entities.GetComponent<SalvageExpeditionDataComponent>(restored).Enabled, Is.False);
            AssertConsoleState(entities, restored, enabled: false, cooldown: true);
            salvage.RestoreGridExpeditions(restored, legacy ? null : false);
            Assert.That(salvage.GridExpeditionsEnabled(restored), Is.False);
            entities.DeleteEntity(restored);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LegacyGridDataRebuildsRuntimeOffersImmediatelyWithoutResettingFutureCooldown()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var gridMetadata = entities.GetComponent<MetaDataComponent>(map.Grid);
            entities.RunMapInit(map.Grid, gridMetadata);
            Assert.That(gridMetadata.EntityLifeStage, Is.EqualTo(EntityLifeStage.MapInitialized));
            var parking = entities.System<ParkingSystem>();
            entities.EnsureComponent<ShuttleComponent>(map.Grid);
            entities.SpawnEntity("ComputerSalvageExpedition", map.GridCoords);
            var source = entities.AddComponent<SalvageExpeditionDataComponent>(map.Grid);
            source.Enabled = true;
            source.Cooldown = true;
            source.NextOffer = pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromMinutes(10);
            source.Missions.Add(42, new SalvageMissionParams { Index = 42, Seed = 1357 });
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            var snapshot = WithoutExpeditionMetadata(parking.SerializeSnapshot(map.Grid));
            var restored = parking.LoadSnapshot(snapshot);
            Assert.That(entities.GetComponent<ParkingShuttleComponent>(restored).ExpeditionsEnabled, Is.Null);
            var data = entities.GetComponent<SalvageExpeditionDataComponent>(restored);
            Assert.That(data.Missions, Is.Not.Empty, "Runtime offers must be rebuilt immediately even while NextOffer is in the future.");
            Assert.That(data.Missions.ContainsKey(42), Is.False, "The source runtime mission list is intentionally not serialized.");
            Assert.That(data.NextOffer, Is.EqualTo(source.NextOffer));
            Assert.That(data.Cooldown, Is.True);
            AssertConsoleState(entities, restored, enabled: true, cooldown: true);
            AssertIdempotent(entities, restored, null);
            entities.DeleteEntity(restored);
        });
        await pair.CleanReturnAsync();
    }

    private static void AssertConsoleState(IEntityManager entities, EntityUid grid, bool enabled, bool cooldown)
    {
        var console = entities.EntityQuery<SalvageExpeditionConsoleComponent, TransformComponent>(true)
            .Single(entry => entry.Item2.GridUid == grid).Item1.Owner;
        Assert.That(entities.System<SharedUserInterfaceSystem>().TryGetUiState<SalvageExpeditionConsoleState>(
            console, SalvageConsoleUiKey.Expedition, out var state), Is.True);
        Assert.That(state!.Missions.Count > 0, Is.EqualTo(enabled));
        Assert.That(state.Cooldown, Is.EqualTo(cooldown));
    }

    private static void AssertIdempotent(IEntityManager entities, EntityUid grid, bool? policy)
    {
        var data = entities.GetComponent<SalvageExpeditionDataComponent>(grid);
        var missions = data.Missions.ToArray();
        var nextOffer = data.NextOffer;
        var nextIndex = data.NextIndex;
        var salvage = entities.System<SalvageSystem>();
        salvage.RestoreGridExpeditions(grid, policy);
        salvage.RestoreGridExpeditions(grid, policy);
        Assert.That(data.Missions.ToArray(), Is.EqualTo(missions));
        Assert.That(data.NextOffer, Is.EqualTo(nextOffer));
        Assert.That(data.NextIndex, Is.EqualTo(nextIndex));
    }

    private static string WithoutExpeditionMetadata(string snapshot)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(snapshot));
        var root = (YamlMappingNode) yaml.Documents.Single().RootNode;
        var gridId = ((YamlSequenceNode) root["grids"]).Children.Single();
        var grid = ((YamlSequenceNode) root["entities"]).Children.Cast<YamlMappingNode>()
            .SelectMany(group => ((YamlSequenceNode) group["entities"]).Children.Cast<YamlMappingNode>())
            .Single(entity => entity["uid"].Equals(gridId));
        var parking = ((YamlSequenceNode) grid["components"]).Children.Cast<YamlMappingNode>()
            .Single(component => component["type"].ToString() == "ParkingShuttle");
        foreach (var key in parking.Children.Keys.Where(key => key.ToString().StartsWith("expedition", StringComparison.Ordinal)).ToArray())
            parking.Children.Remove(key);
        using var writer = new StringWriter();
        yaml.Save(new YamlMappingFix(new Emitter(writer)), assignAnchors: false);
        return writer.ToString();
    }
}

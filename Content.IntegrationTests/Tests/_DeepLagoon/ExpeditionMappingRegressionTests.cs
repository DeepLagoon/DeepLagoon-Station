using System.Linq;
using System.Numerics;
using Content.Server.Decals;
using Content.Server.Salvage.Expeditions.Structure;
using Content.Shared.Decals;
using Content.Shared.Salvage;
using Content.Shared.Salvage.Expeditions;
using Robust.Shared.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Utility;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using ServerExpedition = Content.Server.Salvage.Expeditions.SalvageExpeditionComponent;
using ClientExpedition = Content.Client.Salvage.SalvageExpeditionComponent;

#pragma warning disable RA0002 // Test fixture sets up expedition objectives directly.
namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ExpeditionMappingRegressionTests
{
    [Test]
    public async Task EveryMissionOffersAtLeastThirtyMinutes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var salvage = pair.Server.EntMan.System<SharedSalvageSystem>();
            foreach (var difficulty in Enum.GetValues<DifficultyRating>())
            foreach (var type in Enum.GetValues<SalvageMissionType>())
            for (var seed = 0; seed < 100; seed++)
                Assert.That(salvage.GetMission(type, difficulty, seed).Duration, Is.GreaterThanOrEqualTo(TimeSpan.FromMinutes(30)));
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false, true)]
    [TestCase(true, true)]
    [TestCase(false, false)]
    public async Task FrozenMappingMapReplicatesDecalsAndTheirEdits(bool initialized, bool pvs)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        NetEntity netGrid = default;
        EntityUid grid = default;
        EntityUid map = default;
        uint decal = 0;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(Robust.Shared.CVars.NetPVS, pvs);
            map = maps.CreateMap(out var mapId, runMapInit: initialized);
            maps.SetPaused(mapId, true);
            var created = maps.CreateGridEntity(mapId); grid = created.Owner;
            maps.SetTile(created, Vector2i.Zero, new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId));
            var decals = entities.System<DecalSystem>();
            Assert.That(decals.TryAddDecal("FullTileOverlayGreyscale", new EntityCoordinates(grid, Vector2.Zero), out decal,
                Color.FromHex("#43A8C080"), Angle.FromDegrees(90), 7, true), Is.True);
            if (!initialized)
            {
                var loader = entities.System<MapLoaderSystem>();
                var path = new ResPath("/frozen-decal-regression.yml");
                Assert.That(loader.TrySaveGrid(grid, path), Is.True);
                entities.DeleteEntity(grid);
                Assert.That(loader.TryLoadGrid(mapId, path, out var loaded), Is.True);
                grid = loaded!.Value.Owner;
            }
            netGrid = entities.GetNetEntity(grid);
            var observer = entities.SpawnEntity("MobObserver", new EntityCoordinates(grid, new Vector2(.5f, .5f)));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), observer);
        });
        await pair.RunTicksSync(60);
        async Task Check(string color) => await pair.Client.WaitAssertion(() =>
        {
            var comp = pair.Client.EntMan.GetComponent<DecalGridComponent>(pair.Client.EntMan.GetEntity(netGrid));
            var received = comp.ChunkCollection.ChunkCollection.Values.SelectMany(chunk => chunk.Decals.Values).Single();
            Assert.Multiple(() =>
            {
                Assert.That(received.Color, Is.EqualTo(Color.FromHex(color)));
                Assert.That(received.ZIndex, Is.EqualTo(7));
                Assert.That(received.Angle, Is.EqualTo(Angle.FromDegrees(90)));
                Assert.That(received.Cleanable, Is.True);
            });
        });
        await Check("#43A8C080");
        await pair.Server.WaitAssertion(() => pair.Server.EntMan.System<DecalSystem>().SetDecalColor(grid, decal, Color.Red));
        await pair.RunTicksSync(30);
        await Check("#FF0000FF");
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null);
            pair.Server.EntMan.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ObjectiveCompletionAndDeadlineReplicateToExpeditionCrew()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        NetEntity netMap = default;
        EntityUid map = default;
        EntityUid[] targets = [];
        TimeSpan endTime = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            map = entities.System<SharedMapSystem>().CreateMap(out _);
            netMap = entities.GetNetEntity(map);
            var expedition = entities.AddComponent<ServerExpedition>(map);
            expedition.Stage = ExpeditionStage.Running;
            endTime = expedition.EndTime = pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromMinutes(30);
            expedition.ObjectivesTotal = 2;
            var objectives = entities.AddComponent<SalvageStructureExpeditionComponent>(map);
            targets = Enumerable.Range(0, 2).Select(_ => entities.SpawnEntity(null, new EntityCoordinates(map, Vector2.Zero))).ToArray();
            objectives.Structures.AddRange(targets);
            var observer = entities.SpawnEntity("MobObserver", new EntityCoordinates(map, Vector2.Zero));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), observer);
            entities.Dirty(map, expedition);
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            var state = pair.Client.EntMan.GetComponent<ClientExpedition>(pair.Client.EntMan.GetEntity(netMap));
            Assert.That(state.EndTime, Is.EqualTo(endTime));
            Assert.That(state.ObjectivesTotal, Is.EqualTo(2));
            Assert.That(state.ObjectivesCompleted, Is.Zero);
        });
        await pair.Server.WaitAssertion(() => { foreach (var target in targets) pair.Server.EntMan.DeleteEntity(target); });
        await pair.RunTicksSync(60);
        await pair.Client.WaitAssertion(() => Assert.That(pair.Client.EntMan.GetComponent<ClientExpedition>(
            pair.Client.EntMan.GetEntity(netMap)).ObjectivesCompleted, Is.EqualTo(2), "Both targets destroyed on the same tick must count."));
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null);
            pair.Server.EntMan.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }
}

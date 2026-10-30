using System;
using System.Linq;
using System.Numerics;
using Content.Server.Power.Components;
using Content.Server.Research.Disk;
using Content.Server.Research.Systems;
using Content.Server._DeepLagoon.SpaceEnvironment;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared.Access.Components;
using Content.Shared.Cargo.Components;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.Research.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.Tag;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class SpaceSurveyResearchTests
{
    [Test]
    public async Task FirstSurveysPayOnceAndRespectResearchNetworks()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var cfg = pair.Server.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(SpaceEnvironmentCVars.Campaign, "integration-survey-research");
            cfg.SetCVar(SpaceEnvironmentCVars.SurveyResearchBase, 6500);
            var sectors = entities.System<SectorSystem>();
            var maps = entities.System<SharedMapSystem>();
            var research = entities.System<ResearchSystem>();
            var scan = entities.System<SpaceSurveySystem>();

            foreach (var mode in new[] { "local", "portable", "ambiguous", "connected" })
            {
                var map = maps.CreateMap(out var mapId);
                var region = new SpaceRegion { Id = "signal", Radius = 500, Intensity = 0.5f };
                var sector = new SectorDefinition { Id = "survey-research/" + mode, Regions = new() { region } };
                sectors.Store.GetOrCreate(sectors.Campaign, sector);
                sectors.Bind(map, sector);
                var grid = maps.CreateGridEntity(mapId);
                var foreignGrid = maps.CreateGridEntity(mapId);
                entities.System<SharedTransformSystem>().SetLocalPosition(foreignGrid.Owner, new Vector2(10000, 10000));
                var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
                for (var x = 0; x < 4; x++)
                {
                    maps.SetTile(grid, new Vector2i(x, 0), floor);
                    maps.SetTile(foreignGrid, new Vector2i(x, 0), floor);
                }
                var coordinates = new EntityCoordinates(grid.Owner, new Vector2(0.5f, 0.5f));
                var console = entities.SpawnEntity("ComputerShuttle", coordinates);
                var actor = entities.SpawnEntity("MobHuman", coordinates);
                entities.RemoveComponent<AccessReaderComponent>(console);
                entities.RemoveComponent<ShuttleConsoleLockComponent>(console);
                entities.GetComponent<ApcPowerReceiverComponent>(console).Powered = true;

                EntityUid Server(EntityUid parent)
                {
                    var uid = entities.SpawnEntity(null, new EntityCoordinates(parent, new Vector2(2.5f, 0.5f)));
                    entities.AddComponent<ResearchServerComponent>(uid);
                    Assert.That(entities.GetComponent<TransformComponent>(uid).GridUid, Is.EqualTo(parent));
                    return uid;
                }
                var foreign = Server(foreignGrid.Owner);
                EntityUid? local = mode == "portable" ? null : Server(grid.Owner);
                EntityUid? extra = mode == "ambiguous" ? Server(grid.Owner) : null;
                if (mode == "connected")
                {
                    entities.EnsureComponent<ResearchClientComponent>(console);
                    research.RegisterClient(console, foreign);
                }
                var expected = SpaceSurveyResearchReward.Calculate(region);
                Assert.That(entities.GetComponent<SpaceEnvironmentComponent>(map).Surveyed, Is.Empty);
                Assert.That(scan.Scan(console, actor), Is.True, mode);
                Assert.That(entities.GetComponent<ResearchServerComponent>(foreign).Points, Is.EqualTo(mode == "connected" ? expected : 0), mode);
                if (local != null)
                    Assert.That(entities.GetComponent<ResearchServerComponent>(local.Value).Points, Is.EqualTo(mode == "local" ? expected : 0), mode);
                if (extra != null)
                    Assert.That(entities.GetComponent<ResearchServerComponent>(extra.Value).Points, Is.Zero, mode);
                var disks = entities.EntityQuery<ResearchDiskComponent>().ToArray();
                var portable = mode is "portable" or "ambiguous";
                Assert.That(disks, Has.Length.EqualTo(portable ? 1 : 0), mode);
                if (portable)
                {
                    Assert.That(disks.Single().Points, Is.EqualTo(expected), mode);
                    Assert.That(entities.GetComponent<MetaDataComponent>(disks.Single().Owner).EntityPrototype!.ID, Is.EqualTo("DeepLagoonSpaceSurveyDisk"));
                    Assert.That(entities.GetComponent<StaticPriceComponent>(disks.Single().Owner).Price, Is.Zero);
                    Assert.That(entities.System<TagSystem>().HasTag(disks.Single().Owner, "PointDisk"), Is.False);
                }

                // Rebinding reloads the persistent journal, just like reopening the same sector.
                sectors.Bind(map, sector);
                entities.GetComponent<SpaceSurveyCooldownComponent>(grid.Owner).ReadyAt = TimeSpan.Zero;
                scan.Scan(console, actor);
                Assert.That(entities.GetComponent<SpaceEnvironmentComponent>(map).Surveyed, Is.EquivalentTo(new[] { "signal" }));
                Assert.That(entities.GetComponent<ResearchServerComponent>(foreign).Points, Is.EqualTo(mode == "connected" ? expected : 0));
                if (local != null)
                    Assert.That(entities.GetComponent<ResearchServerComponent>(local.Value).Points, Is.EqualTo(mode == "local" ? expected : 0));
                Assert.That(entities.EntityQuery<ResearchDiskComponent>().Count(), Is.EqualTo(portable ? 1 : 0));
                if (portable)
                {
                    var disk = disks.Single().Owner;
                    // A portable reward redeems through the native disk path, only once even before deletion runs.
                    for (var attempt = 0; attempt < 2; attempt++)
                        entities.EventBus.RaiseLocalEvent(disk, new AfterInteractEvent(actor, disk, foreign, coordinates, canReach: true));
                    Assert.That(entities.GetComponent<ResearchServerComponent>(foreign).Points, Is.EqualTo(expected));
                }
                entities.DeleteEntity(map);
            }
        });
        await pair.CleanReturnAsync();
    }
}

using System.Numerics;
using Content.Server.Salvage;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Salvage.Expeditions;
using Content.Server.Station.Systems;
using Content.Shared.Salvage;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Station.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class ExpeditionDepartureTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task DockedShipCannotClaimExpeditionEvenWithDebugConsole(bool debug)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var map = entities.System<SharedMapSystem>();
            map.CreateMap(out var mapId);
            var ship = map.CreateGridEntity(mapId);
            var other = map.CreateGridEntity(mapId);
            var tile = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            map.SetTile(ship, Vector2i.Zero, tile);
            map.SetTile(ship, new Vector2i(1, 0), tile);
            map.SetTile(other, new Vector2i(1, 0), tile);
            entities.System<SharedTransformSystem>().SetWorldPosition(other, new Vector2(10, 0));
            var station = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            entities.AddComponent<StationDataComponent>(station);
            entities.System<StationSystem>().AddGridToStation(station, ship.Owner);
            var data = entities.AddComponent<SalvageExpeditionDataComponent>(station);
            data.Missions.Add(1, new SalvageMissionParams { Index = 1, MissionType = SalvageMissionType.Elimination });
            var console = entities.SpawnEntity(null, new EntityCoordinates(ship, 0, 0));
            entities.AddComponent<SalvageExpeditionConsoleComponent>(console).Debug = debug;
            var port = entities.SpawnEntity(null, new EntityCoordinates(ship, 1, 0));
            var target = entities.SpawnEntity(null, new EntityCoordinates(other, 1, 0));
            var dock = entities.AddComponent<DockingComponent>(port);
            // An unlocked small grid must block departure as well as a station.
            dock.DockedWith = target;
            try
            {
                var salvage = entities.System<SalvageSystem>();
                var portTransform = entities.GetComponent<TransformComponent>(port);
                Assert.That(portTransform.GridUid, Is.EqualTo(ship.Owner),
                    $"Port parent: {portTransform.ParentUid}; paused: {entities.GetComponent<MetaDataComponent>(port).EntityPaused}; docked: {dock.Docked}");
                Assert.That(salvage.IsExpeditionShuttleDocked(ship), Is.True);
                var claim = new ClaimSalvageMessage { Index = 1 };
                entities.EventBus.RaiseLocalEvent(console, claim);
                Assert.Multiple(() =>
                {
                    Assert.That(data.Claimed, Is.False);
                    Assert.That(data.NextOffer, Is.EqualTo(TimeSpan.Zero));
                    Assert.That(entities.HasComponent<FTLComponent>(ship), Is.False);
                });
                dock.DockedWith = null;
                Assert.That(salvage.IsExpeditionShuttleDocked(ship), Is.False);
                dock.DockedWith = target;
                Assert.That(salvage.IsExpeditionShuttleDocked(ship), Is.True,
                    "A connection established while mission generation yields must also be detected.");

                dock.DockedWith = null;
                var targetDock = entities.AddComponent<DockingComponent>(target);
                var destination = map.CreateMap(out _);
                entities.AddComponent<SalvageExpeditionComponent>(destination);
                var ftl = entities.AddComponent<FTLComponent>(ship);
                ftl.State = FTLState.Starting;
                ftl.TargetCoordinates = new EntityCoordinates(destination, 0, 0);
                var docking = entities.System<DockingSystem>();
                docking.Dock((port, dock), (target, targetDock));
                Assert.That(dock.Docked, Is.False, "A ship preparing expedition FTL must not accept new docking connections.");
                docking.Dock((target, targetDock), (port, dock));
                Assert.That(dock.Docked, Is.False, "The other ship cannot bypass the restriction by initiating docking.");
                entities.RemoveComponent<SalvageExpeditionComponent>(destination);
                docking.Dock((port, dock), (target, targetDock));
                Assert.That(dock.Docked, Is.True, "The restriction must not change ordinary FTL docking.");
                docking.Undock((port, dock));
                entities.RemoveComponent<FTLComponent>(ship);
            }
            finally
            {
                dock.DockedWith = null;
            }
        });
        await pair.CleanReturnAsync();
    }
}

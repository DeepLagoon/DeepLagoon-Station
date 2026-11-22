using System.Linq;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ShuttleParkingSizeTests : Robust.UnitTesting.RobustIntegrationTest
{
    private async Task<ServerIntegrationInstance> StartParkingServer()
    {
        var options = new ServerIntegrationOptions
        {
            ContentStart = true,
            LoadTestAssembly = false,
            ContentAssemblies = PoolManager.Instance.ServerAssemblies,
            Options = new() { LoadConfigAndUserData = false },
        };
        foreach (var (name, value) in PoolManager.Instance.DefaultCvars)
            options.CVarOverrides[name] = value;
        var server = StartServer(options);
        await server.WaitIdleAsync();
        return server;
    }

    [Test]
    public async Task ArgentUsesMediumBerthBeforeAndAfterSnapshotButExtensionsNeedLargerBerths()
    {
        var server = await StartParkingServer();
        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out var mapId);
            var prototype = server.ProtoMan.Index<VesselPrototype>("Argent");
            Assert.That(prototype.Category, Is.EqualTo(VesselSize.Medium));
            Assert.That(entities.System<MapLoaderSystem>().TryLoadGrid(mapId, prototype.ShuttlePath, out var loaded), Is.True);
            var grid = loaded!.Value;
            var parking = entities.System<ParkingSystem>();
            entities.EnsureComponent<VesselComponent>(grid).VesselId = prototype.ID;

            // This path also handles existing ships that predate ParkingShuttleComponent.
            var entityCount = entities.Count<MetaDataComponent>();
            Assert.That(parking.GetCategory(grid), Is.EqualTo(1));
            Assert.That(entities.Count<MetaDataComponent>(), Is.EqualTo(entityCount), "Reading stock geometry must not spawn entities");

            var owner = Guid.NewGuid();
            parking.RegisterPurchasedShip(grid, owner, prototype.Price, prototype.ID, "Капитан");
            Assert.That(parking.GetCategory(grid), Is.EqualTo(1));
            var snapshot = parking.SerializeSnapshot(grid);
            var restored = parking.LoadSnapshot(snapshot);
            Assert.That(parking.GetCategory(restored), Is.EqualTo(1));

            using var store = new ParkingStore(":memory:");
            var purchase = store.BeginPurchase(owner, Guid.NewGuid(), 1);
            store.GrantPurchase(owner, purchase);
            store.Save(owner, purchase.Id, new(Guid.NewGuid(), "Аргент", "Капитан", parking.GetCategory(restored),
                prototype.Price, snapshot, ParkingStore.Hash(snapshot)));
            Assert.That(store.Slot(owner, purchase.Id).Ship!.Category, Is.EqualTo(1));

            var hull = maps.GetAllTiles(grid, grid.Comp).ToArray();
            var highest = hull.Max(tile => tile.GridIndices.Y);
            var bow = hull.First(tile => tile.GridIndices.Y == highest);
            var extension = bow.GridIndices + new Vector2i(0, 1);
            maps.SetTile(grid, extension, bow.Tile);
            Assert.That(parking.GetCategory(grid), Is.EqualTo(2), "Extending the stock bow must exceed a medium berth");
            var extended = parking.LoadSnapshot(parking.SerializeSnapshot(grid));
            Assert.That(parking.GetCategory(extended), Is.EqualTo(2), "Saving must not reset the stock baseline");

            maps.SetTile(grid, extension, Tile.Empty);
            Assert.That(parking.GetCategory(grid), Is.EqualTo(1));
            maps.SetTile(grid, bow.GridIndices + new Vector2i(0, 1000), bow.Tile);
            Assert.That(parking.GetCategory(grid), Is.EqualTo(4), "Large extensions must still require the highest category");
        });
    }

    [Test]
    public async Task PurchasableCatalogModelsHaveUsableStockGeometryWithoutSpawningEntities()
    {
        var server = await StartParkingServer();
        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out var mapId);
            var grid = maps.CreateGridEntity(mapId);
            var floor = new Tile(server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTile(grid, Vector2i.Zero, floor);
            var vessel = entities.AddComponent<VesselComponent>(grid);
            var parking = entities.System<ParkingSystem>();
            var entityCount = entities.Count<MetaDataComponent>();
            var count = 0;
            foreach (var prototype in server.ProtoMan.EnumeratePrototypes<VesselPrototype>()
                         .Where(v => !v.Abstract && v.Purchasable && v.Category != VesselSize.All))
            {
                vessel.VesselId = prototype.ID;
                var expected = prototype.Category switch
                {
                    VesselSize.Micro or VesselSize.Small => 0,
                    VesselSize.Medium => 1,
                    VesselSize.Large => 2,
                    _ => throw new ArgumentOutOfRangeException(),
                };
                Assert.That(parking.GetCategory(grid), Is.EqualTo(expected), prototype.ID);
                count++;
            }
            Assert.That(count, Is.GreaterThan(0));
            Assert.That(entities.Count<MetaDataComponent>(), Is.EqualTo(entityCount));
            TestContext.Out.WriteLine($"Checked stock parking geometry for {count} catalog models.");

            entities.RemoveComponent<VesselComponent>(grid);
            Assert.That(parking.GetCategory(grid), Is.EqualTo(0), "Unregistered grids must still use physical size");
        });
    }
}

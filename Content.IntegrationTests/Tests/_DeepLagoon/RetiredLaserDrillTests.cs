using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Shared._DeepLagoon.Mining;
using Content.Shared._Mono.ShipRepair;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.Store;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class RetiredLaserDrillTests
{
    [Test]
    public async Task LegacyParkingRemovesDrillsNestedFlatpacksAndRepairGhostsButKeepsOtherEquipment()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan; var prototypes = pair.Server.ProtoMan; var parking = e.System<ParkingSystem>();
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var x = -1; x < 6; x++) for (var y = -1; y < 6; y++) e.System<SharedMapSystem>().SetTile(map.Grid, new Vector2i(x, y), floor);
            foreach (var id in new[] { "LaserDrill", "StationLaserDrill", "LaserDrillFlatpack" })
            { Assert.That(prototypes.HasIndex<Robust.Shared.Prototypes.EntityPrototype>(id), Is.True); Assert.That(LaserDrillAvailability.IsRetiredPrototype(prototypes, id), Is.True); }
            Assert.That(prototypes.EnumeratePrototypes<ListingPrototype>().Any(p => p.ProductEntity is { } product && LaserDrillAvailability.IsRetiredPrototype(prototypes, product)), Is.False);
            var drill = e.SpawnEntity("LaserDrill", map.GridCoords);
            e.SpawnEntity("StationLaserDrill", new EntityCoordinates(map.Grid, 2, 2));
            var holder = e.SpawnEntity("ClothingBackpack", new EntityCoordinates(map.Grid, 3, 3));
            var container = e.System<SharedContainerSystem>().EnsureContainer<Container>(holder, "retirement-test");
            var flatpack = e.SpawnEntity("LaserDrillFlatpack", map.GridCoords); var other = e.SpawnEntity("Multitool", map.GridCoords);
            Assert.That(e.System<SharedContainerSystem>().Insert(flatpack, container), Is.True);
            Assert.That(e.System<SharedContainerSystem>().Insert(other, container), Is.True);
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            var data = e.EnsureComponent<ShipRepairDataComponent>(map.Grid);
            data.EntityPalette = ["LaserDrill", "WallSolid", "LaserDrillFlatpack"];
            data.Chunks.Clear(); data.Chunks[Vector2i.Zero] = new() { Tiles = new int[25], Entities = new()
                { [0] = new() { ProtoIndex = 0, LocalPosition = Vector2.Zero }, [1] = new() { ProtoIndex = 1, LocalPosition = new(1, 0) }, [2] = new() { ProtoIndex = 2, LocalPosition = new(2, 0) } } };
            var repair = e.System<SharedShipRepairSystem>();
            Assert.That(repair.GetShipyardRepairs(map.Grid).Any(p => p.Prototype == "LaserDrill"), Is.False);
            var legacy = parking.SerializeSnapshot(map.Grid); Assert.That(legacy, Does.Contain("LaserDrill"));
            e.DeleteEntity(map.Grid);
            var restored = parking.LoadSnapshot(legacy);
            var objects = e.EntityQuery<MetaDataComponent, TransformComponent>(true).Where(v => v.Item2.GridUid == restored).ToArray();
            Assert.That(objects.Any(v => v.Item1.EntityPrototype != null && LaserDrillAvailability.IsRetiredPrototype(prototypes, v.Item1.EntityPrototype.ID)), Is.False);
            Assert.That(objects.Count(v => v.Item1.EntityPrototype?.ID == "ClothingBackpack"), Is.EqualTo(1));
            Assert.That(objects.Count(v => v.Item1.EntityPrototype?.ID == "Multitool"), Is.EqualTo(1));
            var savedRepair = e.GetComponent<ShipRepairDataComponent>(restored);
            Assert.That(savedRepair.EntityPalette.Select(p => p.Id), Is.EqualTo(new[] { "WallSolid" }));
            Assert.That(savedRepair.Chunks[Vector2i.Zero].Entities.Keys, Is.EquivalentTo(new[] { 1 }));
            Assert.That(savedRepair.Chunks[Vector2i.Zero].Entities[1].ProtoIndex, Is.Zero);
            Assert.That(parking.SerializeSnapshot(restored), Does.Not.Contain("LaserDrill"));
            e.DeleteEntity(restored);
        });
        await pair.CleanReturnAsync();
    }
}

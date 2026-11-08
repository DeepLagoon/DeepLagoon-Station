using System.Numerics;
using Content.Shared.Construction.EntitySystems;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Storage;
using Content.Shared.SubFloor;
using Content.Shared.Tools.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class AnchorableStorageTests
{
    [TestCase("Wrench", true)]
    [TestCase("Wrench", false)]
    [TestCase("PowerDrill", true)]
    [TestCase("PowerDrill", false)]
    public async Task UsingAnchoringToolOnCarriedBagInsertsIt(string toolPrototype, bool equipped)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var definitions = pair.Server.ResolveDependency<ITileDefinitionManager>();
            maps.CreateMap(out var mapId);
            var grid = maps.CreateGridEntity(mapId);
            maps.SetTile(grid, Vector2i.Zero, new Tile(definitions["Plating"].TileId));
            var coords = new EntityCoordinates(grid, new Vector2(0.5f, 0.5f));
            var user = entities.SpawnEntity("MobHuman", coords);
            var bag = entities.SpawnEntity("ClothingBackpackHolding", coords);
            var tool = entities.SpawnEntity(toolPrototype, coords);
            var hands = entities.System<SharedHandsSystem>();

            if (toolPrototype == "PowerDrill")
                Assert.That(entities.System<SharedToolSystem>().CycleMultipleTool(tool), Is.True);

            Assert.That(hands.TryPickup(user, tool), Is.True);
            if (equipped)
                Assert.That(entities.System<InventorySystem>().TryEquip(user, bag, "back"), Is.True);
            else
                Assert.That(hands.TryPickupAnyHand(user, bag), Is.True);

            Assert.That(entities.System<SharedInteractionSystem>().InteractUsing(user, tool, bag, coords), Is.True);
            Assert.That(entities.GetComponent<StorageComponent>(bag).StoredItems.ContainsKey(tool), Is.True);
            Assert.That(entities.GetComponent<TransformComponent>(bag).ParentUid, Is.EqualTo(user));
            Assert.That(entities.GetComponent<TransformComponent>(bag).Anchored, Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AnchoringRequiresReleasedBagAndExposedPlatingUntilCompletion()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var entities = pair.Server.EntMan;
        EntityUid user = default;
        EntityUid bag = default;
        EntityUid wrench = default;
        EntityCoordinates coords = default;
        Entity<MapGridComponent> grid = default;
        var bagTile = new Vector2i(1, 0);
        Tile plating = default;
        Tile floor = default;

        await pair.Server.WaitAssertion(() =>
        {
            var maps = entities.System<SharedMapSystem>();
            var definitions = pair.Server.ResolveDependency<ITileDefinitionManager>();
            maps.CreateMap(out var mapId);
            grid = maps.CreateGridEntity(mapId);
            plating = new Tile(definitions["Plating"].TileId);
            floor = new Tile(definitions["FloorSteel"].TileId);
            maps.SetTile(grid, Vector2i.Zero, plating);
            maps.SetTile(grid, bagTile, floor);
            var userCoords = new EntityCoordinates(grid, new Vector2(0.5f, 0.5f));
            coords = new EntityCoordinates(grid, new Vector2(1.5f, 0.5f));
            user = entities.SpawnEntity("MobHuman", userCoords);
            bag = entities.SpawnEntity("ClothingBackpackHolding", coords);
            wrench = entities.SpawnEntity("Wrench", userCoords);
            Assert.That(entities.System<SharedHandsSystem>().TryPickup(user, wrench), Is.True);

            UseWrench();
            Assert.That(IsAnchoring(), Is.False, "Covered floor");
            maps.SetTile(grid, bagTile, new Tile(definitions["PlatingSpace"].TileId));
            UseWrench();
            Assert.That(IsAnchoring(), Is.False, "Exposed to space");

            maps.SetTile(grid, bagTile, plating);
            var pulling = entities.System<PullingSystem>();
            var pullable = entities.GetComponent<PullableComponent>(bag);
            Assert.That(pulling.TryStartPull(user, bag), Is.True);
            // Exercise the direct anchoring entry point without storing the wrench in the bag.
            entities.System<AnchorableSystem>().TryToggleAnchor(bag, user, wrench);
            Assert.That(IsAnchoring(), Is.False, "Bag is being pulled");
            Assert.That(pulling.TryStopPull(bag, pullable, user), Is.True);

            UseWrench();
            Assert.That(IsAnchoring(), Is.True);
            maps.SetTile(grid, bagTile, floor);
        });

        await pair.RunTicksSync(90);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(IsAnchoring(), Is.False);
            Assert.That(entities.GetComponent<TransformComponent>(bag).Anchored, Is.False);

            entities.System<SharedMapSystem>().SetTile(grid, bagTile, plating);
            UseWrench();
            Assert.That(IsAnchoring(), Is.True);
        });

        await pair.RunTicksSync(90);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(entities.GetComponent<TransformComponent>(bag).Anchored, Is.True);
            entities.System<SharedMapSystem>().SetTile(grid, bagTile, floor);
            Assert.That(entities.GetComponent<SubFloorHideComponent>(bag).IsUnderCover, Is.True);
        });
        await pair.CleanReturnAsync();

        void UseWrench() => entities.System<SharedInteractionSystem>().InteractUsing(user, wrench, bag, coords);

        bool IsAnchoring() => entities.TryGetComponent<DoAfterComponent>(user, out var doAfter) && doAfter.DoAfters.Count > 0;
    }
}

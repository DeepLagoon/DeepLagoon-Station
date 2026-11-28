using System.Numerics;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._Mono.ShipRepair;
using Content.Shared._Mono.ShipRepair.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class ShipRepairInterestTests : InteractionTest
{
    [Test]
    public async Task SnapshotArrivesOnlyWithToolAndRefreshesAfterRangeOrEquipmentChanges()
    {
        EntityUid farGrid = default;
        GameTick originalTick = default;
        await Server.WaitAssertion(() =>
        {
            var repair = SEntMan.System<ShipRepairSystem>();
            MapMan.SetTile(MapData.Grid, new Vector2i(1, 0), new Tile(TileMan[Plating].TileId));
            repair.GenerateRepairData(MapData.Grid);
            MapMan.SetTile(MapData.Grid, new Vector2i(1, 0), Tile.Empty);
            var maps = SEntMan.System<SharedMapSystem>();
            var far = maps.CreateGridEntity(MapId);
            farGrid = far.Owner;
            SEntMan.System<SharedTransformSystem>().SetLocalPosition(farGrid, new Vector2(100, 0));
            maps.SetTile(far, Vector2i.Zero, new Tile(TileMan[Plating].TileId));
            repair.GenerateRepairData(farGrid);
            var data = SEntMan.GetComponent<ShipRepairDataComponent>(MapData.Grid);
            originalTick = data.LastModifiedTick;
            Assert.That(SEntMan.CanGetComponentState(SEntMan.EventBus, data, ServerSession), Is.False);
        });
        var nearNet = SEntMan.GetNetEntity(MapData.Grid);
        await RunTicks(20);
        await Client.WaitAssertion(() => Assert.That(CEntMan.HasComponent<ShipRepairDataComponent>(CEntMan.GetEntity(nearNet)), Is.False));

        var repairTool = await PlaceInHands("ShipRepairDeviceAdmin");
        await RunTicks(20);
        await Server.WaitAssertion(() =>
        {
            var near = SEntMan.GetComponent<ShipRepairDataComponent>(MapData.Grid);
            var far = SEntMan.GetComponent<ShipRepairDataComponent>(farGrid);
            Assert.That(SEntMan.CanGetComponentState(SEntMan.EventBus, near, ServerSession), Is.True);
            Assert.That(SEntMan.CanGetComponentState(SEntMan.EventBus, far, ServerSession), Is.False);
            Assert.That(near.LastModifiedTick, Is.GreaterThan(originalTick));
        });
        await Client.WaitAssertion(() => Assert.That(CEntMan.GetComponent<ShipRepairDataComponent>(CEntMan.GetEntity(nearNet)).Chunks, Is.Not.Empty));

        await Interact((EntityUid?)null, new EntityCoordinates(MapData.Grid, new Vector2(1.5f, .5f)));
        await Server.WaitAssertion(() => Assert.That(MapMan.GetTileRef(MapData.Grid, MapData.Grid.Comp,
            new Vector2i(1, 0)).Tile.IsEmpty, Is.False, "Repairing a snapshotted tile must still work."));

        await Server.WaitPost(() => Transform.SetCoordinates(SPlayer, new EntityCoordinates(farGrid, new Vector2(.5f, .5f))));
        await RunTicks(20);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.CanGetComponentState(SEntMan.EventBus,
                SEntMan.GetComponent<ShipRepairDataComponent>(MapData.Grid), ServerSession), Is.False);
            Assert.That(SEntMan.CanGetComponentState(SEntMan.EventBus,
                SEntMan.GetComponent<ShipRepairDataComponent>(farGrid), ServerSession), Is.True);
        });
        var farNet = SEntMan.GetNetEntity(farGrid);
        await Client.WaitAssertion(() => Assert.That(CEntMan.GetComponent<ShipRepairDataComponent>(CEntMan.GetEntity(farNet)).Chunks, Is.Not.Empty));

        await Drop();
        await RunTicks(20);
        await Server.WaitAssertion(() =>
        {
            var data = SEntMan.GetComponent<ShipRepairDataComponent>(farGrid);
            originalTick = data.LastModifiedTick;
            Assert.That(SEntMan.CanGetComponentState(SEntMan.EventBus, data, ServerSession), Is.False);
        });
        await Pickup(repairTool);
        await RunTicks(20);
        await Server.WaitAssertion(() =>
        {
            var data = SEntMan.GetComponent<ShipRepairDataComponent>(farGrid);
            Assert.That(SEntMan.CanGetComponentState(SEntMan.EventBus, data, ServerSession), Is.True);
            Assert.That(data.LastModifiedTick, Is.GreaterThan(originalTick), "Re-equipping must refresh a previously cached snapshot.");
        });
    }
}

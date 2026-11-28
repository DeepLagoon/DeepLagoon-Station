using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Numerics;
using System.Reflection;
using Content.Client._DeepLagoon.WebUI;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server._Mono.ShipRepair;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.Tag;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ParkingShipRepairTests : InteractionTest
{
    [Test]
    public async Task SnapshotConsoleEnforcesOwnershipPowerRepairLockAndPerShipTwentyMinuteCooldown()
    {
        await Server.WaitAssertion(() =>
        {
            var repairs = SEntMan.System<ShipRepairSystem>();
            var parking = SEntMan.System<ParkingSystem>();
            SEntMan.System<TagSystem>().AddTag(SPlayer, "CanPilot");
            parking.RegisterPurchasedShip(MapData.Grid, ServerSession.UserId.UserId, 100, "", "Captain");
            var first = SEntMan.SpawnEntity("ComputerShuttle", new EntityCoordinates(MapData.Grid, new Vector2(.5f, .5f)));
            MapMan.SetTile(MapData.Grid, new Vector2i(1, 0), new Tile(TileMan[Plating].TileId));
            var second = SEntMan.SpawnEntity("ComputerShuttle", new EntityCoordinates(MapData.Grid, new Vector2(1.5f, .5f)));
            foreach (var console in new[] { first, second })
            {
                SEntMan.RemoveComponent<ShuttleConsoleLockComponent>(console);
                SEntMan.RemoveComponent<Content.Shared.Access.Components.AccessReaderComponent>(console);
            }
            repairs.GenerateRepairData(MapData.Grid);
            var data = SEntMan.GetComponent<ShipRepairDataComponent>(MapData.Grid);
            var initial = data.SnapshotVersion;
            void Save(EntityUid console) => SEntMan.EventBus.RaiseLocalEvent(console,
                new ShuttleRepairSnapshotMessage { Actor = SPlayer, UiKey = ShuttleConsoleUiKey.Key });
            SEntMan.GetComponent<ApcPowerReceiverComponent>(first).Powered = false;
            Save(first);
            Assert.That(data.SnapshotVersion, Is.EqualTo(initial));
            foreach (var console in new[] { first, second })
            {
                SEntMan.System<PowerReceiverSystem>().SetNeedsPower(console, false);
                SEntMan.GetComponent<ApcPowerReceiverComponent>(console).Powered = true;
            }
            Save(first);
            Assert.That(data.SnapshotVersion, Is.Not.EqualTo(initial));
            Assert.That(data.SnapshotReadyAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Is.InRange(1195000, 1200000));
            var saved = data.SnapshotVersion;
            Save(second);
            Assert.That(data.SnapshotVersion, Is.EqualTo(saved), "Another console cannot bypass the ship cooldown.");
            data.SnapshotReadyAt = 0;
            data.ShipyardLocked = true;
            Save(second);
            Assert.That(data.SnapshotVersion, Is.EqualTo(saved));
            Assert.That(data.SnapshotReadyAt, Is.Zero, "A rejected save must not start a cooldown.");
            data.ShipyardLocked = false;
            SEntMan.GetComponent<ParkingShuttleComponent>(MapData.Grid).OwnerAccount = Guid.NewGuid();
            Save(first);
            Assert.That(data.SnapshotVersion, Is.EqualTo(saved), "A pilot cannot overwrite somebody else's blueprint.");
        });
    }

    [Test]
    public async Task ShipyardUsesHandheldBlueprintAndRepairsOnlySelectedMissingParts()
    {
        await Server.WaitAssertion(() =>
        {
            var repairs = SEntMan.System<ShipRepairSystem>();
            MapMan.SetTile(MapData.Grid, new Vector2i(1, 0), new Tile(TileMan[Plating].TileId));
            MapMan.SetTile(MapData.Grid, new Vector2i(2, 0), new Tile(TileMan[Plating].TileId));
            var wall = SEntMan.SpawnEntity("WallShuttle", new EntityCoordinates(MapData.Grid, new Vector2(1.5f, .5f)));
            repairs.GenerateRepairData(MapData.Grid);
            Assert.That(repairs.GetShipyardRepairs(MapData.Grid), Is.Empty);
            SEntMan.DeleteEntity(wall);
            MapMan.SetTile(MapData.Grid, new Vector2i(2, 0), Tile.Empty);
            var missing = repairs.GetShipyardRepairs(MapData.Grid);
            Assert.That(missing.Count, Is.EqualTo(2));
            var entity = missing.Single(p => p.Kind != "tiles");
            Assert.That(entity.Prototype, Is.EqualTo("WallShuttle"));
            Assert.That(repairs.ApplyShipyardRepair(MapData.Grid, entity), Is.Not.Null);
            Assert.That(repairs.GetShipyardRepairs(MapData.Grid).Single().Kind, Is.EqualTo("tiles"));
            repairs.ApplyShipyardRepair(MapData.Grid, missing.Single(p => p.Kind == "tiles"));
            Assert.That(repairs.GetShipyardRepairs(MapData.Grid), Is.Empty);
        });
    }

    [Test]
    public async Task ExistingOriginalFragmentIsReattachedWithoutDuplicatingItsContents()
    {
        await Server.WaitAssertion(() =>
        {
            var repairs = SEntMan.System<ShipRepairSystem>();
            var wall = SEntMan.SpawnEntity("WallShuttle", new EntityCoordinates(MapData.Grid, new Vector2(.5f, .5f)));
            var child = SEntMan.SpawnEntity("Crowbar", new EntityCoordinates(wall, Vector2.Zero));
            repairs.GenerateRepairData(MapData.Grid);
            var fragment = MapMan.CreateGridEntity(MapId);
            MapMan.SetTile(fragment, Vector2i.Zero, new Tile(TileMan[Plating].TileId));
            SEntMan.EnsureComponent<ShipRepairFragmentComponent>(fragment).SourceGrids.Add(SEntMan.GetNetEntity(MapData.Grid));
            Transform.Unanchor(wall);
            Transform.SetCoordinates(wall, new EntityCoordinates(fragment, new Vector2(.5f, .5f)));
            Assert.That(Transform.AnchorEntity(wall), Is.True);
            var part = repairs.GetShipyardRepairs(MapData.Grid).Single();
            Assert.That(part.Original, Is.EqualTo(SEntMan.GetNetEntity(wall)));
            Assert.That(part.Charges, Is.Zero, "Reattaching an existing part must not bill newly manufactured repair matter.");
            Assert.That(repairs.ApplyShipyardRepair(MapData.Grid, part), Is.EqualTo(wall));
            Assert.That(SEntMan.GetComponent<TransformComponent>(wall).GridUid, Is.EqualTo(MapData.Grid.Owner));
            Assert.That(SEntMan.GetComponent<TransformComponent>(child).ParentUid, Is.EqualTo(wall));
            Assert.That(repairs.GetShipyardRepairs(MapData.Grid), Is.Empty);
        });
    }

    [Test]
    public async Task RepairLockProtectsBlueprintAndParkingPreservesCooldownAndProvenance()
    {
        await Server.WaitAssertion(() =>
        {
            var repairs = SEntMan.System<ShipRepairSystem>();
            var parking = SEntMan.System<ParkingSystem>();
            var wall = SEntMan.SpawnEntity("WallShuttle", new EntityCoordinates(MapData.Grid, new Vector2(.5f, .5f)));
            parking.RegisterPurchasedShip(MapData.Grid, Guid.NewGuid(), 100, "", "Captain");
            repairs.GenerateRepairData(MapData.Grid);
            parking.CaptureRepairProvenance(MapData.Grid);
            var data = SEntMan.GetComponent<ShipRepairDataComponent>(MapData.Grid);
            data.SnapshotReadyAt = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds();
            var version = data.SnapshotVersion;
            data.ShipyardLocked = true;
            repairs.GenerateRepairData(MapData.Grid);
            Assert.That(data.SnapshotVersion, Is.EqualTo(version));
            data.ShipyardLocked = false;
            var loaded = parking.LoadSnapshot(parking.SerializeSnapshot(MapData.Grid));
            var restored = SEntMan.GetComponent<ShipRepairDataComponent>(loaded);
            Assert.That(restored.SnapshotReadyAt, Is.EqualTo(data.SnapshotReadyAt));
            Assert.That(restored.SnapshotVersion, Is.EqualTo(version));
            Assert.That(restored.Chunks.Values.SelectMany(c => c.Entities.Values).Single().ParkingOriginalIdentity, Is.Not.EqualTo(Guid.Empty));
            SEntMan.DeleteEntity(loaded);
        });
    }

    [Test]
    public async Task NativeRepairTabLaysOutAtNarrowAndRegularWindowWidths()
    {
        await Client.WaitAssertion(() =>
        {
            var panel = new NativeInterfacePanel((_, _) => { }, () => { });
            typeof(NativeInterfacePanel).GetField("_parkingTab", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(panel, 3);
            var state = "{\"id\":true,\"repairEnabled\":true,\"repair\":{\"ship\":\"test\",\"parked\":true,\"parts\":[{\"id\":\"t:0:0\",\"name\":\"Палуба\",\"kind\":\"tiles\",\"x\":0.5,\"y\":0.5}],\"current\":[],\"visuals\":[]}}";
            panel.UpdateState("ShuttleParking", state);
            foreach (var width in new[] { 320, 720 })
            {
                panel.Measure(new Vector2(width, 720));
                panel.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(width, 720)));
                Assert.That(panel.Size.X, Is.EqualTo(width));
            }
            var controls = Descendants(panel).ToArray();
            Assert.That(controls.OfType<CheckBox>().Any(c => c.Text.Contains("Палуба")), Is.True);
            Assert.That(controls.OfType<Button>().Any(c => c.Text == "Рассчитать выбранный ремонт"), Is.True);
            Assert.That(controls.OfType<Button>().Any(c => c.Text == "Починить всё"), Is.True);
            Assert.That(controls.OfType<Content.Client.UserInterface.Controls.MapGridControl>().Any(), Is.True,
                "Repair uses the status map's native pan/zoom control rather than a sprite collage.");
        });
    }

    [Test]
    public async Task RepairAllOpensAFloatingQuoteWithoutChargingUntilItsExactTokenIsConfirmed()
    {
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        var sent = new List<(string Action, string Payload)>();
        NativeInterfacePanel panel = null!;
        IUserInterfaceManager ui = null!;
        void Click(Button button) => ((Action<BaseButton.ButtonEventArgs>)typeof(BaseButton).GetField("OnPressed", hidden)!.GetValue(button)!).Invoke(null!);
        var parts = "\"parts\":[{\"id\":\"t:0:0\",\"name\":\"Палуба\",\"kind\":\"tiles\",\"geometry\":\"floor\",\"tile\":true,\"x\":0.5,\"y\":0.5},{\"id\":\"e:0:0\",\"name\":\"Стена\",\"kind\":\"structures\",\"geometry\":\"wall\",\"x\":0.5,\"y\":1.5}],\"current\":[],\"visuals\":[]";
        string State(string quote) => "{\"id\":true,\"repairEnabled\":true,\"repair\":{\"ship\":\"owned\",\"parked\":true," + parts + "," + quote + "}}";
        await Client.WaitAssertion(() =>
        {
            panel = new NativeInterfacePanel((action, payload) => sent.Add((action, payload)), () => { });
            ui = Client.ResolveDependency<IUserInterfaceManager>();
            ui.StateRoot.AddChild(panel);
            typeof(NativeInterfacePanel).GetField("_parkingTab", hidden)!.SetValue(panel, 3);
            panel.UpdateState("ShuttleParking", State("\"selectionVersion\":\"v1\",\"token\":\"\""));
            Click(Descendants(panel).OfType<Button>().Single(b => b.Text == "Починить всё"));
        });
        // Native actions deliberately run after UI traversal, since they may rebuild the tree.
        // Exercise the real frame/deferred-action queue rather than bypassing that protection.
        await Client.WaitRunTicks(1);
        await Client.WaitAssertion(() =>
        {
            Assert.That(sent, Has.Count.EqualTo(1));
            Assert.That(sent[0].Action, Is.EqualTo("repairQuote"));
            Assert.That(sent[0].Payload, Does.Contain("\"ranges\":\"0-1\""));
            Assert.That(sent[0].Payload, Does.Contain("\"selectionVersion\":\"v1\""));
            panel.UpdateState("ShuttleParking", State("\"selectionVersion\":\"v2\",\"token\":\"quote-all\",\"selected\":\"t:0:0|e:0:0\",\"materials\":\"100\",\"credits\":\"125\",\"coins\":\"2\",\"seconds\":3600"));
            var window = (Content.Client.UserInterface.Controls.FancyWindow)typeof(NativeInterfacePanel).GetField("_parkingRepairConfirmation", hidden)!.GetValue(panel)!;
            Assert.That(window, Is.Not.Null);
            Assert.That(window.Parent, Is.EqualTo(ui.WindowRoot), "Confirmation is a floating window outside the repair tab's scroll area.");
            Assert.That(sent, Has.Count.EqualTo(1), "A server quote must never automatically debit either balance.");
            var buttons = Descendants(window).OfType<Button>().ToArray();
            Assert.That(buttons.Any(b => b.Text == "Мгновенно за 2 LC"), Is.True);
            Click(buttons.Single(b => b.Text == "Оплатить 125 кр."));
        });
        await Client.WaitRunTicks(1);
        await Client.WaitAssertion(() =>
        {
            Assert.That(sent, Has.Count.EqualTo(2));
            Assert.That(sent[1].Action, Is.EqualTo("repairConfirm"));
            Assert.That(sent[1].Payload, Does.Contain("\"token\":\"quote-all\""));
            Assert.That(sent[1].Payload, Does.Contain("\"coins\":false"));
            Assert.That(typeof(NativeInterfacePanel).GetField("_parkingRepairConfirmation", hidden)!.GetValue(panel), Is.Null);
            panel.Orphan(); panel.Dispose();
        });
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (var child in root.Children) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
}

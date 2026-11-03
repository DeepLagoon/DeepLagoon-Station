using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._Mono.FireControl;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared._Crescent.Hardpoints;
using Content.Shared._Mono.FireControl;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Power;
using Content.Shared.Power.Components;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NF;

[TestFixture]
public sealed class ImportedShipGunneryTests
{
    private sealed class ShotListener : EntitySystem
    {
        public readonly HashSet<EntityUid> Fired = new();

        public override void Initialize()
        {
            SubscribeLocalEvent<HardpointAnchorableOnlyComponent, AmmoShotEvent>((uid, _, ev) =>
            {
                if (ev.FiredProjectiles.Count > 0)
                    Fired.Add(uid);
            });
        }
    }

    [TestCase("Europa", 12)]
    [TestCase("Tethys", 6)]
    [TestCase("Spyglass", 6)]
    [TestCase("Anaconda", 9)]
    [TestCase("Gungnir", 6)]
    [TestCase("flashpoint", 11)]
    [TestCase("gixon", 17)]
    [TestCase("Kairiis", 2)]
    [TestCase("BMPraetorian", 8)]
    public async Task ImportedWeaponsMountAndConnectToTheGunneryConsole(string vesselId, int expectedWeapons)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var server = pair.Server;
        var entities = server.EntMan;
        var shotWeapons = new List<EntityUid>();
        EntityUid loadedMap = default;

        await server.WaitAssertion(() =>
        {
            var map = entities.System<SharedMapSystem>();
            var mapUid = map.CreateMap(out var mapId);
            loadedMap = mapUid;
            var vessel = server.ResolveDependency<IPrototypeManager>().Index<VesselPrototype>(vesselId);
            Assert.That(entities.System<MapLoaderSystem>().TryLoadGrid(mapId, vessel.ShuttlePath, out var grid), Is.True);
            var gridUid = grid!.Value.Owner;
            var weapons = new List<EntityUid>();
            var guns = entities.EntityQueryEnumerator<HardpointAnchorableOnlyComponent, TransformComponent>();
            while (guns.MoveNext(out var uid, out var mount, out var transform))
            {
                if (transform.GridUid != gridUid || !transform.Anchored)
                    continue;
                weapons.Add(uid);
                Assert.That(mount.anchoredTo, Is.Not.Null, $"{vesselId}: {entities.ToPrettyString(uid)} has no compatible mount");
                Assert.That(entities.GetComponent<HardpointComponent>(mount.anchoredTo!.Value).anchoring,
                    Is.EqualTo(uid), $"{vesselId}: the mount must belong to this weapon");
            }
            Assert.That(weapons, Has.Count.EqualTo(expectedWeapons));

            // Supply electricity explicitly: fresh ships can have their reactors switched off.
            // Mounting, processing limits and server/console registration use the real systems.
            var receivers = entities.EntityQueryEnumerator<ApcPowerReceiverComponent, TransformComponent>();
            while (receivers.MoveNext(out var uid, out var receiver, out var transform))
            {
                if (transform.GridUid != gridUid)
                    continue;
                if (!entities.HasComponent<FireControlServerComponent>(uid)
                    && !entities.HasComponent<FireControllableComponent>(uid)
                    && !entities.HasComponent<FireControlConsoleComponent>(uid))
                    continue;
                entities.System<PowerReceiverSystem>().SetNeedsPower(uid, false, receiver);
                receiver.Powered = true;
                var power = new PowerChangedEvent(true, receiver.Load);
                entities.EventBus.RaiseLocalEvent(uid, ref power);
            }
            var fireControl = entities.System<FireControlSystem>();
            fireControl.ForceServerReconnectionOnGrid(gridUid);
            fireControl.RefreshControllables(gridUid);
            var control = entities.GetComponent<FireControlGridComponent>(gridUid);
            Assert.That(control.ControllingServer, Is.Not.Null);
            var controlServer = entities.GetComponent<FireControlServerComponent>(control.ControllingServer!.Value);
            Assert.That(controlServer.Controlled.IsSupersetOf(weapons), Is.True, $"{vesselId}: some weapons are missing from the server");
            Assert.That(controlServer.UsedProcessingPower, Is.LessThanOrEqualTo(controlServer.ProcessingPower));
            Assert.That(controlServer.Consoles, Is.Not.Empty);
            var console = controlServer.Consoles.First();
            var state = fireControl.GetConsoleState(console)!;
            Assert.That(state.Connected, Is.True);
            Assert.That(weapons.All(uid => state.FireControllables.Any(entry => entry.NetEntity == entities.GetNetEntity(uid))), Is.True);

            if (vesselId == "Europa")
            {
                entities.System<ShotListener>();
                var batteries = entities.System<BatterySystem>();
                var shooter = entities.SpawnEntity("MobHuman", new EntityCoordinates(gridUid, Vector2.Zero));
                foreach (var weapon in weapons)
                {
                    if (entities.TryGetComponent<BatteryComponent>(weapon, out var battery))
                        batteries.SetCharge(weapon, battery.MaxCharge, battery);
                    var transform = entities.GetComponent<TransformComponent>(weapon);
                    var target = transform.LocalPosition + transform.LocalRotation.RotateVec(-Vector2.UnitY) * 200;
                    fireControl.FireWeapons(control.ControllingServer.Value, [entities.GetNetEntity(weapon)],
                        entities.GetNetCoordinates(new EntityCoordinates(gridUid, target)), shooter, firingConsole: console);
                    shotWeapons.Add(weapon);
                }
            }
        });
        if (shotWeapons.Count > 0)
        {
            await server.WaitRunTicks(10);
            await server.WaitAssertion(() =>
            {
                foreach (var weapon in shotWeapons)
                    Assert.That(entities.System<ShotListener>().Fired, Does.Contain(weapon),
                        $"{entities.ToPrettyString(weapon)} did not fire through its server");
            });
        }
        await server.WaitPost(() => entities.DeleteEntity(loadedMap));
        await pair.CleanReturnAsync();
    }
}

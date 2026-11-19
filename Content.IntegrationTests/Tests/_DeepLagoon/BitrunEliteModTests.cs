using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Modsuits;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.PowerCell;
using Content.Server.Temperature.Components;
using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared._Goobstation.Clothing.Components;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Armor;
using Content.Shared.Clothing;
using Content.Shared.Clothing.EntitySystems;
using Content.Shared.Cuffs;
using Content.Shared.Damage;
using Content.Shared.Explosion;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Movement.Components;
using Content.Shared.Power.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BitrunEliteModTests
{
    [Test]
    public async Task EliteRequiresSourceAccessDeploysAllPartsAndRunsItsInstalledModules()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        var em = pair.Server.EntMan;
        EntityUid user = default, suit = default, cell = default, jump = default, helmet = default, hat = default;
        NetEntity hatNet = default;
        await pair.Server.WaitAssertion(() =>
        {
            var maps = em.System<SharedMapSystem>();
            for (var x = -4; x <= 4; x++)
            for (var y = -4; y <= 4; y++) maps.SetTile(map.Grid, new Vector2i(x, y), map.Tile.Tile);
            user = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            em.RemoveComponent<RespiratorComponent>(user);
            em.RemoveComponent<BarotraumaComponent>(user);
            em.RemoveComponent<TemperatureComponent>(user);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), user);
            suit = em.SpawnEntity("BitrunSourceObjItemModControlPreEquippedElite", map.GridCoords);
            var control = em.GetComponent<ModControlComponent>(suit);
            Assert.That(control.Capacity, Is.EqualTo(19));
            Assert.That(control.Modules.Count, Is.EqualTo(12));
            Assert.That(em.System<InventorySystem>().TryEquip(user, suit, "back", force: true), Is.True);
            Deploy();
            Assert.That(control.Deployment, Is.Null, "A user without Syndicate access cannot deploy the elite suit.");
            var id = em.SpawnEntity("SyndicateIDCard", map.GridCoords);
            Assert.That(em.System<InventorySystem>().TryEquip(user, id, "id", force: true), Is.True);
            Assert.That(em.System<PowerCellSystem>().TryGetBatteryFromSlot(suit, out var battery, out _), Is.True);
            cell = battery!.Value;
            Assert.That(em.GetComponent<BatteryComponent>(cell).MaxCharge, Is.EqualTo(400000));
            Deploy();
            Assert.That(control.Deployment, Is.Not.Null);
            jump = control.Modules.ContainedEntities.Single(uid => em.GetComponent<ModModuleComponent>(uid).Mode == ModModuleMode.JumpJet);
        });
        await pair.RunSeconds(4);
        await pair.Server.WaitAssertion(() => Send(ModOperation.ToggleSuit));
        await pair.RunSeconds(5);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.GetComponent<SealableClothingControlComponent>(suit).IsCurrentlySealed, Is.True);
            Assert.That(em.System<ToggleableClothingSystem>().GetAttachedToggleStatus(suit), Is.EqualTo(ToggleableClothingAttachedStatus.AllToggled));
            Assert.That(em.System<InventorySystem>().TryGetSlotEntity(user, "shoes", out var boots), Is.True);
            Assert.That(em.GetComponent<SealableClothingComponent>(boots!.Value).IsSealed, Is.True);
            Assert.That(em.HasComponent<MagbootsComponent>(boots.Value), Is.False, "The source elite kit has no magboot module.");
            Assert.That(em.HasComponent<Content.Shared.Slippery.NoSlipComponent>(boots.Value), Is.False, "Sealing must not grant an uninstalled anti-slip module.");
            Assert.That(em.System<SharedCuffableSystem>().GetCuffingTime(user, 4), Is.EqualTo(3));
            var result = em.System<DamageableSystem>().TryChangeDamage(user,
                new DamageSpecifier { DamageDict = { ["Blunt"] = 100 } }, canSever: false, targetPart: TargetBodyPart.Torso);
            Assert.That(result!.GetTotal().Float(), Is.EqualTo(40).Within(0.01f));
            var bomb = new GetExplosionResistanceEvent("Default");
            em.EventBus.RaiseLocalEvent(user, ref bomb);
            Assert.That(bomb.DamageCoefficient, Is.EqualTo(0.45f).Within(0.001f));
            Assert.That(em.HasComponent<ModMagneticHarnessComponent>(user), Is.True);
            var weapon = em.SpawnEntity("BitrunSourceObjItemGunEnergyRechargeKineticAccelerator", map.GridCoords);
            Assert.That(em.System<SharedHandsSystem>().TryPickupAnyHand(user, weapon), Is.True);
            Assert.That(em.System<SharedHandsSystem>().TryDrop(user, weapon), Is.True);
            Assert.That(em.System<ModSystem>().StartJumpJet((suit, em.GetComponent<ModControlComponent>(suit)),
                (jump, em.GetComponent<ModModuleComponent>(jump)), user), Is.True);
        });
        await pair.RunSeconds(1.5f);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.System<InventorySystem>().TryGetSlotEntity(user, "suitstorage", out _), Is.True,
                em.TryGetComponent<ModMagneticHarnessComponent>(user, out var harness) ? harness.LastReturnFailure : "The wearer lost the active harness effect.");
            Assert.That(em.HasComponent<ModJumpJetFlightComponent>(user), Is.True);
            Assert.That(em.GetComponent<MovementIgnoreGravityComponent>(user).Weightless, Is.True);
            Assert.That(em.GetComponent<ModModuleComponent>(jump).NextUse, Is.GreaterThan(pair.Server.Timing.CurTime));
            Assert.That(em.GetComponent<BatteryComponent>(cell).CurrentCharge, Is.LessThan(399750));
            Assert.That(em.System<ModSystem>().StartJumpJet((suit, em.GetComponent<ModControlComponent>(suit)),
                (jump, em.GetComponent<ModModuleComponent>(jump)), user), Is.False);
            Assert.That(em.System<InventorySystem>().TryGetSlotEntity(user, "head", out var head), Is.True);
            helmet = head!.Value;
            hat = em.SpawnEntity("BitrunSourceObjItemClothingHeadCowboy", em.GetComponent<TransformComponent>(user).Coordinates);
            Assert.That(em.System<SharedHandsSystem>().TryPickupAnyHand(user, hat), Is.True);
            Assert.That(em.System<ModSystem>().MountHat(helmet, user, hat), Is.True);
            hatNet = em.GetNetEntity(hat);
        });
        await pair.RunSeconds(1);
        await pair.Client.WaitAssertion(() =>
        {
            var client = pair.Client.EntMan;
            var hatId = client.GetEntity(hatNet);
            Assert.That(client.HasComponent<ModMountedHatComponent>(hatId), Is.True);
            var visual = new GetEquipmentVisualsEvent(client.GetComponent<TransformComponent>(hatId).ParentUid, "helmetcover");
            client.EventBus.RaiseLocalEvent(hatId, visual);
            Assert.That(visual.Layers.Count, Is.GreaterThan(0), "The mounted hat uses its actual worn head sprites.");
        });
        await pair.RunSeconds(5);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.HasComponent<ModJumpJetFlightComponent>(user), Is.False);
            Send(ModOperation.ToggleSuit);
        });
        await pair.RunSeconds(5);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.System<SharedCuffableSystem>().GetCuffingTime(user, 4), Is.EqualTo(4));
            Assert.That(em.HasComponent<ModMagneticHarnessComponent>(user), Is.False);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null);
        });
        await pair.CleanReturnAsync();

        void Deploy() => em.EventBus.RaiseLocalEvent(suit, new ModSuitActionEvent { Performer = user, Kind = ModActionKind.Deploy, Alternate = true });
        void Send(ModOperation operation)
        {
            var message = new ModUiMessage(operation) { Actor = user, UiKey = ModUiKey.Key };
            em.EventBus.RaiseLocalEvent(suit, message);
        }
    }
}

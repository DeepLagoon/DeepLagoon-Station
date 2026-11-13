using System.Linq;
using Content.Server._DeepLagoon.Modsuits;
using Content.Server.Construction;
using Content.Server.Power.EntitySystems;
using Content.Server.PowerCell;
using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared._Goobstation.Clothing.Components;
using Content.Shared.Clothing.EntitySystems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Storage;
using Content.Shared.Wires;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class ModSuitTests
{
    [Test]
    public async Task SuitServicesModulesAndUnsealsWhenItsBatteryRunsOut()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var em = pair.Server.EntMan;
        EntityUid wearer = default, suit = default, cell = default, flashlight = default;

        await pair.Server.WaitAssertion(() =>
        {
            wearer = em.SpawnEntity("MobHuman", map.GridCoords);
            suit = em.SpawnEntity("ClothingModsuitStandartPowerCell", map.GridCoords);

            var inventory = em.System<InventorySystem>();
            var hands = em.System<SharedHandsSystem>();
            var interaction = em.System<SharedInteractionSystem>();
            var wires = em.System<SharedWiresSystem>();
            var panel = em.GetComponent<WiresPanelComponent>(suit);
            var control = em.GetComponent<ModControlComponent>(suit);
            var storage = control.Modules.ContainedEntities.Single(uid =>
                em.GetComponent<ModModuleComponent>(uid).Family == "storage");
            flashlight = control.Modules.ContainedEntities.Single(uid =>
                em.GetComponent<ModModuleComponent>(uid).Mode == ModModuleMode.Flashlight);
            var extended = em.SpawnEntity("ModModuleStorageExtended", map.GridCoords);
            var crowbar = em.SpawnEntity("Crowbar", map.GridCoords);
            var pocket = em.GetComponent<StorageComponent>(extended).Container;
            var containers = em.System<SharedContainerSystem>();
            Assert.That(containers.Insert(crowbar, pocket), Is.False, "An uninstalled module cannot store equipment.");

            Assert.That(inventory.TryEquip(wearer, suit, "back", silent: true, force: true), Is.True);
            Assert.That(em.System<PowerCellSystem>().TryGetBatteryFromSlot(suit, out var battery, out _), Is.True);
            cell = battery!.Value;
            Assert.That(hands.TryPickupAnyHand(wearer, extended), Is.True);
            Assert.That(wires.TogglePanel(suit, panel, true, wearer), Is.True);

            interaction.InteractUsing(wearer, extended, suit, map.GridCoords);
            Assert.That(control.Modules.Contains(extended), Is.False, "Upgraded storage conflicts with standard storage.");
            Send(ModOperation.RemoveModule, em.GetNetEntity(storage));
            Assert.That(control.Modules.Contains(storage), Is.False);

            interaction.InteractUsing(wearer, extended, suit, map.GridCoords);
            Assert.That(control.Modules.Contains(extended), Is.True);
            Assert.That(em.GetComponent<ModModuleComponent>(extended).Host, Is.EqualTo(suit));
            Assert.That(containers.Insert(crowbar, pocket), Is.True);
            Send(ModOperation.RemoveModule, em.GetNetEntity(extended));
            Assert.That(control.Modules.Contains(extended), Is.True, "Removing storage must preserve its contents.");
            Assert.That(containers.Remove(crowbar, pocket), Is.True);

            Assert.That(wires.TogglePanel(suit, panel, false, wearer), Is.True);
            Send(ModOperation.ToggleSuit);
        });

        await pair.RunTicksSync(200);
        await pair.Server.WaitAssertion(() =>
        {
            var seal = em.GetComponent<SealableClothingControlComponent>(suit);
            Assert.That(seal.IsCurrentlySealed, Is.True);
            Assert.That(em.System<ToggleableClothingSystem>().GetAttachedToggleStatus(suit),
                Is.EqualTo(ToggleableClothingAttachedStatus.AllToggled));

            Send(ModOperation.ToggleModule, em.GetNetEntity(flashlight));
            Assert.That(em.GetComponent<ModModuleComponent>(flashlight).Active, Is.True);
            Assert.That(em.GetComponent<PointLightComponent>(suit).Enabled, Is.True);
            em.System<BatterySystem>().SetCharge(cell, 0);
        });

        await pair.RunTicksSync(40);
        await pair.Server.WaitAssertion(() =>
        {
            var seal = em.GetComponent<SealableClothingControlComponent>(suit);
            Assert.That(seal.IsCurrentlySealed || seal.IsInProcess, Is.False);
            Assert.That(em.GetComponent<ModModuleComponent>(flashlight).Active, Is.False);
            Assert.That(em.GetComponent<PointLightComponent>(suit).Enabled, Is.False);
        });

        await pair.CleanReturnAsync();

        void Send(ModOperation operation, NetEntity target = default)
        {
            var message = new ModUiMessage(operation, target) { Actor = wearer, UiKey = ModUiKey.Key };
            em.EventBus.RaiseLocalEvent(suit, message);
        }
    }

    [Test]
    public async Task AssembledControllerRetainsTheCoreInstalledInItsShell()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var em = pair.Server.EntMan;

        await pair.Server.WaitAssertion(() =>
        {
            var shell = em.SpawnEntity("ModsuitShell", map.GridCoords);
            var core = em.SpawnEntity("ModsuitCoreStandard", map.GridCoords);
            var containers = em.System<SharedContainerSystem>();
            var construction = em.System<ConstructionSystem>();
            Assert.That(containers.TryGetContainer(shell, ModControlComponent.CoreSlot, out var slot), Is.True);
            Assert.That(containers.Insert(core, slot!), Is.True);
            Assert.That(construction.ChangeNode(shell, null, "standard"), Is.True);

            var controller = em.EntityQuery<ModControlComponent>().Single().Owner;
            Assert.That(containers.TryGetContainer(controller, ModControlComponent.CoreSlot, out var installed), Is.True);
            Assert.That(installed!.ContainedEntities, Is.EqualTo(new[] { core }));
        });

        await pair.CleanReturnAsync();
    }
}

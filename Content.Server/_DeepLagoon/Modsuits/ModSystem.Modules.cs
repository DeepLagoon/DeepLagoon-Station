using Content.Server.Movement.Systems;
using Content.Server.Temperature.Components;
using Content.Server.Temperature.Systems;
using Content.Shared.Atmos;
using Content.Shared.Clothing;
using Content.Shared.Damage;
using Content.Shared.Inventory;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;

namespace Content.Server._DeepLagoon.Modsuits;

public sealed partial class ModSystem
{
    private const string StowedToolSlot = "mod-stowed-tool";

    [Dependency] private SharedStorageSystem _storage = default!;
    [Dependency] private SharedPointLightSystem _light = default!;
    [Dependency] private SharedMagbootsSystem _magboots = default!;
    [Dependency] private ItemToggleSystem _toggle = default!;
    [Dependency] private JetpackSystem _jetpack = default!;
    [Dependency] private MovementSpeedModifierSystem _speed = default!;
    [Dependency] private TemperatureSystem _temperature = default!;

    private void OnDamageModify(Entity<ModControlComponent> suit, ref InventoryRelayedEvent<DamageModifyEvent> args)
    {
        if (!IsSealed(suit))
            return;

        foreach (var uid in suit.Comp.Modules.ContainedEntities)
        {
            if (!TryComp<ModModuleComponent>(uid, out var module) || !module.Active ||
                module.DamageCoefficients.Count == 0)
                continue;

            var modifiers = new DamageModifierSet { Coefficients = module.DamageCoefficients };
            args.Args.Damage = DamageSpecifier.ApplyModifierSet(args.Args.Damage,
                DamageSpecifier.PenetrateArmor(modifiers, args.Args.ArmorPenetration));
        }
    }

    private void UseModule(Entity<ModControlComponent> suit, Entity<ModModuleComponent> module, EntityUid user)
    {
        var comp = module.Comp;

        if (comp.Active)
        {
            StopModule(module);
            return;
        }

        if (_timing.CurTime < comp.NextUse || !TryGetEffectTarget(suit, comp, user, out var target))
            return;

        if (!_power.HasCharge(suit, Math.Max(comp.UseCharge, comp.Draw * TickInterval), user: user))
            return;

        var activated = comp.Mode switch
        {
            ModModuleMode.Tool => DeployTool(module, user),
            ModModuleMode.Dispense => Dispense(module, user),
            ModModuleMode.Flashlight => EnableLight(suit, comp),
            ModModuleMode.Magboots => EnableMagboots(target, user, comp),
            ModModuleMode.Jetpack => EnableJetpack(suit, user, comp),
            ModModuleMode.Cloak => EnableCloak(module, user),
            ModModuleMode.Toggle or ModModuleMode.ThermalRegulator => EnableEffects(target, comp, user),
            _ => false,
        };

        if (activated)
        {
            comp.NextUse = _timing.CurTime + comp.UseDelay;
            if (!_power.TryUseCharge(suit, comp.UseCharge))
                StopModule(module);
        }
    }

    private bool TryGetEffectTarget(EntityUid suit, ModModuleComponent module, EntityUid user, out EntityUid target)
    {
        target = suit;
        if (module.Slot == null)
            return true;

        if (!_inventory.TryGetSlotEntity(user, module.Slot, out var equipped) ||
            !TryComp<Content.Shared.Clothing.Components.AttachedClothingComponent>(equipped, out var attached) ||
            attached.AttachedUid != suit)
            return false;

        target = equipped.Value;
        return true;
    }

    private void OnModuleStorageInteract(Entity<ModModuleComponent> module, ref StorageInteractAttemptEvent args)
    {
        if (module.Comp.Mode == ModModuleMode.Storage && module.Comp.Host == null)
            args.Cancelled = true;
    }

    private void OnModuleStorageUiOpened(Entity<ModModuleComponent> module, ref BoundUIOpenedEvent args)
    {
        if (Equals(args.UiKey, StorageComponent.StorageUiKey.Key) &&
            (module.Comp.Host is not {} host || !CanUse(host, args.Actor)))
            _ui.CloseUi(module.Owner, StorageComponent.StorageUiKey.Key, args.Actor);
    }

    private void OnModuleStorageInsert(Entity<ModModuleComponent> module, ref ContainerIsInsertingAttemptEvent args)
    {
        if (module.Comp.Mode == ModModuleMode.Storage && module.Comp.Host == null &&
            args.Container.ID == StorageComponent.ContainerId)
            args.Cancel();
    }

    private void CloseModuleStorage(EntityUid suit)
    {
        if (!TryComp<ModControlComponent>(suit, out var control))
            return;

        foreach (var item in control.Modules.ContainedEntities)
        {
            if (HasComp<StorageComponent>(item))
                _ui.CloseUi(item, StorageComponent.StorageUiKey.Key);
        }
    }

    private void OpenModuleStorage(EntityUid module, EntityUid user)
    {
        _storage.OpenStorageUI(module, user, silent: false);
    }

    private bool Dispense(Entity<ModModuleComponent> module, EntityUid user)
    {
        if (module.Comp.Dispense is not {} proto)
            return false;

        var item = Spawn(proto, Transform(user).Coordinates);
        _hands.TryPickupAnyHand(user, item);
        return true;
    }

    private bool DeployTool(Entity<ModModuleComponent> module, EntityUid user)
    {
        if (module.Comp.Tool is not {} proto)
            return false;

        var slot = _containers.EnsureContainer<ContainerSlot>(module, StowedToolSlot);
        var tool = module.Comp.DeployedTool ?? slot.ContainedEntity;
        if (tool == null || TerminatingOrDeleted(tool.Value))
        {
            tool = Spawn(proto, Transform(user).Coordinates);
            EnsureComp<ModLinkedToolComponent>(tool.Value).Module = module;
            module.Comp.DeployedTool = tool;
            if (!_containers.Insert(tool.Value, slot))
            {
                QueueDel(tool);
                return false;
            }
        }

        module.Comp.DeployedTool = tool;
        if (!_hands.TryPickupAnyHand(user, tool.Value))
        {
            Notify(module, user, "mod-free-hand");
            return false;
        }

        module.Comp.Active = true;
        return true;
    }

    private void OnToolInsertAttempt(Entity<ModLinkedToolComponent> tool, ref ContainerGettingInsertedAttemptEvent args)
    {
        var moduleUid = tool.Comp.Module;
        if (args.Container.Owner == moduleUid && args.Container.ID == StowedToolSlot)
            return;

        if (!TryComp<ModModuleComponent>(moduleUid, out var module) ||
            module.Host is not {} host || !IsWorn(host, args.Container.Owner) ||
            !_hands.TryGetHand(args.Container.Owner, args.Container.ID, out _))
            args.Cancel();
    }

    private bool EnableLight(EntityUid suit, ModModuleComponent module)
    {
        if (!TryComp<PointLightComponent>(suit, out var light))
            return false;

        _light.SetEnabled(suit, true, light);
        module.Active = true;
        return true;
    }

    private bool EnableMagboots(EntityUid boots, EntityUid user, ModModuleComponent module)
    {
        if (!HasComp<MagbootsComponent>(boots) || !_toggle.TryActivate(boots, user, predicted: false))
            return false;

        module.EffectTarget = boots;
        module.Active = true;
        return true;
    }

    private bool EnableJetpack(EntityUid suit, EntityUid user, ModModuleComponent module)
    {
        if (_jetpack.IsUserFlying(user) || HasComp<JetpackComponent>(suit))
            return false;

        EnsureComp<ModPoweredJetpackComponent>(suit);
        var jetpack = AddComp<JetpackComponent>(suit);
        _jetpack.SetEnabled(suit, jetpack, true, user);
        module.Active = HasComp<ActiveJetpackComponent>(suit);
        if (!module.Active)
        {
            RemComp<JetpackComponent>(suit);
            RemComp<ModPoweredJetpackComponent>(suit);
        }

        return module.Active;
    }

    private bool EnableEffects(EntityUid target, ModModuleComponent module, EntityUid user)
    {
        if (module.Active)
            return true;

        foreach (var (name, entry) in module.Effects)
        {
            var type = entry.Component.GetType();
            if (EntityManager.HasComponent(target, type))
                continue;

            EntityManager.AddComponents(target, new Robust.Shared.Prototypes.ComponentRegistry { [name] = entry }, removeExisting: false);
            if (EntityManager.TryGetComponent(target, type, out var applied))
                module.AppliedEffects.Add(applied);
        }

        module.EffectTarget = target;
        module.Active = true;
        _speed.RefreshMovementSpeedModifiers(user, alsoFriction: true);
        return true;
    }

    private void StopModule(Entity<ModModuleComponent> module)
    {
        var comp = module.Comp;
        if (!comp.Active)
            return;

        comp.Active = false;
        var host = comp.Host;
        var target = comp.EffectTarget;
        comp.EffectTarget = null;
        var wearer = TryComp<ModLinkComponent>(host, out var link) ? link.Wearer : null;

        if (comp.Mode == ModModuleMode.Tool && comp.DeployedTool is {} tool && !TerminatingOrDeleted(tool))
        {
            if (TerminatingOrDeleted(module) ||
                !_containers.Insert(tool, _containers.EnsureContainer<ContainerSlot>(module, StowedToolSlot)))
                QueueDel(tool);
        }

        if (comp.Mode == ModModuleMode.Flashlight && host is {} controller && TryComp<PointLightComponent>(controller, out var light))
            _light.SetEnabled(controller, false, light);

        if (comp.Mode == ModModuleMode.Jetpack && host is {} jetpackUid &&
            TryComp<JetpackComponent>(jetpackUid, out var jetpack) && HasComp<ModPoweredJetpackComponent>(jetpackUid))
        {
            if (jetpack.JetpackUser is {} user)
                _jetpack.SetEnabled(jetpackUid, jetpack, false, user);
            RemComp<JetpackComponent>(jetpackUid);
            RemComp<ModPoweredJetpackComponent>(jetpackUid);
        }

        if (comp.Mode == ModModuleMode.Cloak && target is {} cloaked)
            RemComp<ModCloakedComponent>(cloaked);

        if (target is {} attached && !TerminatingOrDeleted(attached))
        {
            if (comp.Mode == ModModuleMode.Magboots)
                _toggle.TryDeactivate(attached, wearer, predicted: false);

            foreach (var effect in comp.AppliedEffects)
            {
                if (EntityManager.TryGetComponent(attached, effect.GetType(), out var current) && ReferenceEquals(effect, current))
                    EntityManager.RemoveComponent(attached, effect);
            }
        }

        comp.AppliedEffects.Clear();
        if (wearer is {} wearerUid)
            _speed.RefreshMovementSpeedModifiers(wearerUid, alsoFriction: true);
    }

    private void StopModules(Entity<ModControlComponent> suit)
    {
        foreach (var uid in suit.Comp.Modules.ContainedEntities)
        {
            if (TryComp<ModModuleComponent>(uid, out var module))
                StopModule((uid, module));
        }
    }

    private void UpdateModules(Entity<ModControlComponent> suit, EntityUid user)
    {
        if (!IsWorn(suit, user) || _mobState.IsDead(user) || !IsSealed(suit) || !HasCore(suit) ||
            !_power.HasCharge(suit, 0.01f))
        {
            StopModules(suit);
            return;
        }

        foreach (var uid in suit.Comp.Modules.ContainedEntities)
        {
            if (!TryComp<ModModuleComponent>(uid, out var module))
                continue;

            if (!TryGetEffectTarget(suit, module, user, out var target) ||
                module.Active && module.Mode != ModModuleMode.Cloak && module.EffectTarget != null && module.EffectTarget != target ||
                module.Active && module.Mode == ModModuleMode.Tool && !_hands.IsHolding(user, module.DeployedTool) ||
                module.Active && module.Mode == ModModuleMode.Jetpack && !HasComp<ActiveJetpackComponent>(suit) ||
                module.Active && module.Mode == ModModuleMode.Magboots && !_toggle.IsActivated(target))
            {
                StopModule((uid, module));
                continue;
            }

            if (!module.Active && module.Mode != ModModuleMode.Passive)
                continue;

            if (module.Draw > 0 && !_power.TryUseCharge(suit, module.Draw * TickInterval))
            {
                StopModule((uid, module));
                continue;
            }

            if (!module.Active)
                EnableEffects(target, module, user);

            if (module.Mode == ModModuleMode.ThermalRegulator && TryComp<TemperatureComponent>(user, out var temperature))
            {
                var difference = Atmospherics.T0C + module.TargetTemperature - temperature.CurrentTemperature;
                var adjustment = Math.Clamp(difference * 0.1f, -0.5f, 0.5f);
                if (MathF.Abs(adjustment) > 0.01f)
                    _temperature.ForceChangeTemperature(user, temperature.CurrentTemperature + adjustment, temperature);
            }
        }
    }
}

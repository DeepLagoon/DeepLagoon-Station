using Content.Shared.Damage;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Stealth.Components;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Physics.Events;

namespace Content.Server._DeepLagoon.Modsuits;

[RegisterComponent]
public sealed partial class ModCloakedComponent : Component
{
    public EntityUid Module;
}

public sealed partial class ModSystem
{
    private void InitializeCloak()
    {
        SubscribeLocalEvent<ModCloakedComponent, DamageChangedEvent>(OnCloakDamaged);
        SubscribeLocalEvent<ModCloakedComponent, MeleeAttackEvent>(OnCloakAttack);
        SubscribeLocalEvent<ModCloakedComponent, SelfBeforeGunShotEvent>(OnCloakShot);
        SubscribeLocalEvent<ModCloakedComponent, InteractHandEvent>(OnCloakTouched);
        SubscribeLocalEvent<ModCloakedComponent, StartCollideEvent>(OnCloakCollision);
    }

    private bool EnableCloak(Entity<ModModuleComponent> module, EntityUid user)
    {
        if (HasComp<StealthComponent>(user) || HasComp<StealthOnMoveComponent>(user))
            return false;

        var cloak = AddComp<ModCloakedComponent>(user);
        cloak.Module = module;
        return EnableEffects(user, module.Comp, user);
    }

    private void OnCloakDamaged(Entity<ModCloakedComponent> wearer, ref DamageChangedEvent args)
    {
        if (args.DamageIncreased)
            BreakCloak(wearer);
    }

    private void OnCloakAttack(Entity<ModCloakedComponent> wearer, ref MeleeAttackEvent args)
    {
        BreakCloak(wearer);
    }

    private void OnCloakShot(Entity<ModCloakedComponent> wearer, ref SelfBeforeGunShotEvent args)
    {
        if (!args.Cancelled && args.Ammo.Count > 0)
            BreakCloak(wearer);
    }

    private void OnCloakTouched(Entity<ModCloakedComponent> wearer, ref InteractHandEvent args)
    {
        if (args.User != wearer.Owner)
            BreakCloak(wearer);
    }

    private void OnCloakCollision(Entity<ModCloakedComponent> wearer, ref StartCollideEvent args)
    {
        if (HasComp<MobStateComponent>(args.OtherEntity))
            BreakCloak(wearer);
    }

    private void BreakCloak(Entity<ModCloakedComponent> wearer)
    {
        var uid = wearer.Comp.Module;
        if (!TryComp<ModModuleComponent>(uid, out var module) || !module.Active)
            return;

        StopModule((uid, module));
        module.NextUse = _timing.CurTime + module.UseDelay;
        if (module.Host is {} suit)
        {
            _power.TryUseCharge(suit, module.UseCharge);
            Notify(suit, wearer, "mod-cloak-broken");
        }
    }
}

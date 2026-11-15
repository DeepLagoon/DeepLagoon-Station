using Content.Shared.Damage;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Stealth;
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

[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class ModCloakRevealComponent : Component
{
    [AutoPausedField]
    public TimeSpan EndTime;

    public StealthComponent Stealth = default!;
    public StealthOnMoveComponent Fade = default!;
}

public sealed partial class ModSystem
{
    private static readonly TimeSpan CloakTransitionTime = TimeSpan.FromSeconds(2);

    [Dependency] private SharedStealthSystem _stealth = default!;

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
        if (TryComp<ModCloakRevealComponent>(user, out var reveal) &&
            TryComp<StealthComponent>(user, out var stealth) && ReferenceEquals(stealth, reveal.Stealth) &&
            TryComp<StealthOnMoveComponent>(user, out var fade) && ReferenceEquals(fade, reveal.Fade))
        {
            var visibility = _stealth.GetVisibility(user, stealth);
            SetCloakFade(user, stealth, fade, visibility, stealth.MinVisibility);
            module.Comp.AppliedEffects.Add(stealth);
            module.Comp.AppliedEffects.Add(fade);
        }
        else if (HasComp<StealthComponent>(user) || HasComp<StealthOnMoveComponent>(user))
        {
            return false;
        }

        RemComp<ModCloakRevealComponent>(user);
        var cloak = AddComp<ModCloakedComponent>(user);
        cloak.Module = module;
        return EnableEffects(user, module.Comp, user);
    }

    private void RevealCloak(EntityUid wearer, ModModuleComponent module)
    {
        if (!TryComp<StealthComponent>(wearer, out var stealth) || !module.AppliedEffects.Contains(stealth) ||
            !TryComp<StealthOnMoveComponent>(wearer, out var fade) || !module.AppliedEffects.Contains(fade))
            return;

        var visibility = _stealth.GetVisibility(wearer, stealth);
        if (visibility >= 1f)
            return;

        SetCloakFade(wearer, stealth, fade, visibility, 1f);
        AddComp(wearer, new ModCloakRevealComponent
        {
            EndTime = _timing.CurTime + CloakTransitionTime,
            Stealth = stealth,
            Fade = fade,
        });
        module.AppliedEffects.Remove(stealth);
        module.AppliedEffects.Remove(fade);
    }

    private void SetCloakFade(EntityUid wearer, StealthComponent stealth, StealthOnMoveComponent fade, float from, float to)
    {
        _stealth.SetVisibility(wearer, from, stealth);
        fade.PassiveVisibilityRate = (to - from) / (float) CloakTransitionTime.TotalSeconds;
        Dirty(wearer, fade);
    }

    private void UpdateCloakReveals()
    {
        var query = EntityQueryEnumerator<ModCloakRevealComponent>();
        while (query.MoveNext(out var wearer, out var reveal))
        {
            if (_timing.CurTime < reveal.EndTime)
                continue;

            if (TryComp<StealthOnMoveComponent>(wearer, out var fade) && ReferenceEquals(fade, reveal.Fade))
                RemComp<StealthOnMoveComponent>(wearer);
            if (TryComp<StealthComponent>(wearer, out var stealth) && ReferenceEquals(stealth, reveal.Stealth))
                RemComp<StealthComponent>(wearer);
            RemComp<ModCloakRevealComponent>(wearer);
        }
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

        StopModule((uid, module), revealCloak: false);
        module.NextUse = _timing.CurTime + module.UseDelay;
        if (module.Host is {} suit)
        {
            _power.TryUseCharge(suit, module.UseCharge);
            Notify(suit, wearer, "mod-cloak-broken");
        }
    }
}

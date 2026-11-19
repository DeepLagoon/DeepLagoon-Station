using Content.Shared.Damage.Systems;
using Content.Shared.Interaction;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.Stunnable;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunTelescopicBatonComponent : Component
{
    public TimeSpan NextStun;
}

[RegisterComponent]
public sealed partial class BitrunCommandWhipComponent : Component;

[RegisterComponent]
public sealed partial class BitrunCommandedComponent : Component
{
    public TimeSpan Until;
    public bool Removing;
}

public sealed class BitrunSpecialMeleeSystem : EntitySystem
{
    [Dependency] private Content.Server._DeepLagoon.Modsuits.ModSystem _mods = default!;
    [Dependency] private ItemToggleSystem _toggle = default!;
    [Dependency] private StaminaSystem _stamina = default!;
    [Dependency] private SharedStunSystem _stun = default!;
    [Dependency] private MovementSpeedModifierSystem _movement = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunTelescopicBatonComponent, MeleeHitEvent>(OnBatonHit);
        SubscribeLocalEvent<BitrunCommandWhipComponent, AfterInteractEvent>(OnCommand);
        SubscribeLocalEvent<BitrunCommandedComponent, RefreshMovementSpeedModifiersEvent>(OnSpeed);
    }

    private void OnBatonHit(Entity<BitrunTelescopicBatonComponent> baton, ref MeleeHitEvent args)
    {
        if (!_toggle.IsActivated(baton.Owner) || baton.Comp.NextStun > _timing.CurTime || args.HitEntities.Count == 0)
            return;
        baton.Comp.NextStun = _timing.CurTime + TimeSpan.FromSeconds(4);
        foreach (var target in args.HitEntities)
        {
            var armor = new Content.Shared.Armor.CoefficientQueryEvent(Content.Shared.Inventory.SlotFlags.WITHOUT_POCKET);
            RaiseLocalEvent(target, armor);
            var coefficient = armor.DamageModifiers.Coefficients.GetValueOrDefault("Blunt", 1);
            // Silver telescopic baton penetrates 30 points of melee armor.
            _stamina.TakeStaminaDamage(target, 55 * MathF.Min(1, coefficient + 0.3f), source: args.User, with: baton);
            if (!_mods.AbsorbBatonKnockdown(target))
                _stun.TryKnockdown(target, TimeSpan.FromSeconds(1.5), true);
        }
    }

    private void OnCommand(Entity<BitrunCommandWhipComponent> whip, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target is not { } target || target == args.User ||
            !HasComp<Content.Shared.Mobs.Components.MobStateComponent>(target))
            return;
        args.Handled = true;
        var commanded = EnsureComp<BitrunCommandedComponent>(target);
        commanded.Removing = false;
        commanded.Until = _timing.CurTime + TimeSpan.FromSeconds(7);
        _movement.RefreshMovementSpeedModifiers(target);
    }

    private void OnSpeed(Entity<BitrunCommandedComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        if (!ent.Comp.Removing)
            args.ModifySpeed(1.25f, 1.25f);
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<BitrunCommandedComponent>();
        while (query.MoveNext(out var uid, out var commanded))
        {
            if (commanded.Until > _timing.CurTime || commanded.Removing)
                continue;
            commanded.Removing = true;
            _movement.RefreshMovementSpeedModifiers(uid);
            RemCompDeferred<BitrunCommandedComponent>(uid);
        }
    }
}

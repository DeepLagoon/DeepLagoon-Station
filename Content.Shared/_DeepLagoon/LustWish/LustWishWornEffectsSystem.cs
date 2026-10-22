using Content.Shared.Eye.Blinding.Systems;
using Content.Shared.Interaction.Events;
using Content.Shared.Interaction;
using Content.Shared.Hands;
using Content.Shared.Item;
using Content.Shared.Movement.Systems;
using Robust.Shared.GameStates;

namespace Content.Shared._DeepLagoon.LustWish;

public sealed partial class LustWishWornEffectsSystem : EntitySystem
{
    [Dependency] private BlindableSystem _blindable = default!;
    [Dependency] private MovementSpeedModifierSystem _speed = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<LustWishWornEffectsComponent, CanSeeAttemptEvent>((EntityUid uid, LustWishWornEffectsComponent c, CanSeeAttemptEvent ev) => { if (c.Blind) ev.Cancel(); });
        SubscribeLocalEvent<LustWishWornEffectsComponent, PickupAttemptEvent>((EntityUid uid, LustWishWornEffectsComponent c, PickupAttemptEvent ev) => { if (c.HandsBlocked) ev.Cancel(); });
        SubscribeLocalEvent<LustWishWornEffectsComponent, UseAttemptEvent>((EntityUid uid, LustWishWornEffectsComponent c, UseAttemptEvent ev) => { if (c.HandsBlocked) ev.Cancel(); });
        SubscribeLocalEvent<LustWishWornEffectsComponent, AttackAttemptEvent>((EntityUid uid, LustWishWornEffectsComponent c, AttackAttemptEvent ev) => { if (c.HandsBlocked) ev.Cancel(); });
        SubscribeLocalEvent<LustWishWornEffectsComponent, InteractionAttemptEvent>((EntityUid uid, LustWishWornEffectsComponent c, ref InteractionAttemptEvent ev) => { if (c.HandsBlocked) ev.Cancelled = true; });
        SubscribeLocalEvent<LustWishWornEffectsComponent, RefreshMovementSpeedModifiersEvent>((EntityUid uid, LustWishWornEffectsComponent c, RefreshMovementSpeedModifiersEvent ev) => { if (c.LegsRestricted) ev.ModifySpeed(0.5f); if (c.Focused) ev.ModifySpeed(0.9f); });
        SubscribeLocalEvent<LustWishWornEffectsComponent, AfterAutoHandleStateEvent>((EntityUid uid, LustWishWornEffectsComponent c, ref AfterAutoHandleStateEvent ev) => Refresh(uid));
    }

    public void Refresh(EntityUid wearer)
    {
        _blindable.UpdateIsBlind(wearer);
        _speed.RefreshMovementSpeedModifiers(wearer);
    }
}

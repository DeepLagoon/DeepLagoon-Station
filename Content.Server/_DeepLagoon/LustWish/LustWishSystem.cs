using Content.Server.Popups;
using Content.Server._DeepLagoon.InteractionPanel;
using Content.Shared._DeepLagoon.LustWish;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Timing;
using Content.Shared.VendingMachines;

namespace Content.Server._DeepLagoon.LustWish;

public sealed partial class LustWishSystem : EntitySystem
{
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private InteractionPanelArousalSystem _arousal = default!;
    [Dependency] private UseDelaySystem _delay = default!;
    [Dependency] private PopupSystem _popup = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<LustWishVendorComponent, MapInitEvent>(OnVendorInit);
        SubscribeLocalEvent<LustWishVendorComponent, InteractUsingEvent>(OnCard);
        SubscribeLocalEvent<LustWishArousalItemComponent, UseInHandEvent>(OnPersonalUse);
    }

    private void OnVendorInit(EntityUid uid, LustWishVendorComponent component, MapInitEvent args)
    {
        if (!TryComp<VendingMachineComponent>(uid, out var vendor)) return;
        if (!component.FreeVending) component.OriginalRequiresCash = vendor.RequiresCash;
        if (component.FreeVending) vendor.RequiresCash = false;
    }

    private void OnCard(EntityUid uid, LustWishVendorComponent component, InteractUsingEvent args)
    {
        if (args.Handled || !HasComp<LustWishDiscountCardComponent>(args.Used) ||
            !_hands.IsHolding(args.User, args.Used, out _) || !_interaction.InRangeAndAccessible(args.User, uid) ||
            !TryComp<VendingMachineComponent>(uid, out var vendor)) return;
        args.Handled = true;
        component.FreeVending = !component.FreeVending;
        vendor.RequiresCash = !component.FreeVending && component.OriginalRequiresCash;
        Dirty(uid, vendor);
        _popup.PopupEntity(Loc.GetString(component.FreeVending ? "dl-lustwish-free-on" : "dl-lustwish-free-off"), uid, args.User);
    }

    private void OnPersonalUse(EntityUid uid, LustWishArousalItemComponent component, UseInHandEvent args)
    {
        if (args.Handled || !_hands.IsHolding(args.User, uid, out _) || _delay.IsDelayed(uid)) return;
        args.Handled = true;
        _delay.TryResetDelay(uid);
        _arousal.AdjustArousal(args.User, args.User, component.Amount);
        _popup.PopupEntity(Loc.GetString("dl-lustwish-personal-use"), args.User, args.User);
    }
}

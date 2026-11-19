using Content.Server.Power.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.Bitrunning;

[DataDefinition]
public sealed partial class BitrunVoucherReward
{
    [DataField(required: true)] public LocId Name;
    [DataField(required: true)] public List<EntProtoId> Items = new();
}

[RegisterComponent]
public sealed partial class BitrunEquipmentVoucherComponent : Component
{
    [DataField] public List<BitrunVoucherReward> Rewards = new();
    [DataField] public bool Redeemed;
}

[RegisterComponent]
public sealed partial class BitrunVoucherRedeemerComponent : Component;

/// <summary>Source single-use vouchers redeem a chosen whole set at the mining console.</summary>
public sealed class BitrunVoucherSystem : EntitySystem
{
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunVoucherRedeemerComponent, GetVerbsEvent<InteractionVerb>>(OnVerbs);
        SubscribeLocalEvent<BitrunEquipmentVoucherComponent, AfterInteractEvent>(OnUse);
    }

    private void OnUse(Entity<BitrunEquipmentVoucherComponent> voucher, ref AfterInteractEvent args)
    {
        if (!args.Handled && args.CanReach && args.Target is { } target && HasComp<BitrunVoucherRedeemerComponent>(target))
        {
            args.Handled = true;
            _popup.PopupEntity(Loc.GetString("bitrun-voucher-choose"), target, args.User);
        }
    }

    private void OnVerbs(Entity<BitrunVoucherRedeemerComponent> terminal, ref GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.Using is not { } voucher ||
            !TryComp<BitrunEquipmentVoucherComponent>(voucher, out var comp) || comp.Redeemed) return;
        var user = args.User;
        for (var index = 0; index < comp.Rewards.Count; index++)
        {
            var choice = index;
            args.Verbs.Add(new InteractionVerb { Text = Loc.GetString(comp.Rewards[index].Name),
                Category = new VerbCategory(Loc.GetString("bitrun-voucher-redeem"), null),
                Act = () => Redeem(terminal, voucher, user, choice) });
        }
    }

    public bool Redeem(EntityUid terminal, EntityUid voucher, EntityUid user, int choice)
    {
        if (Deleted(terminal) || Deleted(voucher) || Deleted(user) || !HasComp<BitrunVoucherRedeemerComponent>(terminal) ||
            !TryComp<BitrunEquipmentVoucherComponent>(voucher, out var comp) || comp.Redeemed ||
            choice < 0 || choice >= comp.Rewards.Count || !_hands.IsHolding(user, voucher, out _) ||
            !_interaction.InRangeUnobstructed(user, terminal) ||
            TryComp<ApcPowerReceiverComponent>(terminal, out var power) && !power.Powered) return false;
        comp.Redeemed = true;
        foreach (var item in comp.Rewards[choice].Items) Spawn(item, Transform(terminal).Coordinates);
        QueueDel(voucher);
        return true;
    }
}

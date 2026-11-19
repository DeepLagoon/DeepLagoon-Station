using Content.Shared.Fluids.Components;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Slippery;
using Robust.Shared.GameStates;

namespace Content.Shared._DeepLagoon.Bitrunning;

[RegisterComponent, NetworkedComponent]
public sealed partial class BitrunBananaShoesComponent : Component
{
    // WhiteMoon: sheet=100 material units, smallest object=10 units.
    [DataField] public int BananasPerSheet = 10;
    [DataField] public int Capacity = 1000;
    [DataField] public int Fuel;
    [DataField] public EntityUid? Wearer;
}

/// <summary>Only wet floors are resisted; banana peels and super-slippery lube still work.</summary>
public sealed class BitrunBananaShoesSystem : EntitySystem
{
    [Dependency] private ItemToggleSystem _toggle = default!;

    public override void Initialize() => SubscribeLocalEvent<BitrunBananaShoesComponent, SlipAttemptEvent>(OnSlip);

    private void OnSlip(EntityUid uid, BitrunBananaShoesComponent component, ref SlipAttemptEvent args)
    {
        if (_toggle.IsActivated(uid) && args.Cause is { } cause && HasComp<PuddleComponent>(cause) &&
            (!TryComp<SlipperyComponent>(cause, out var slippery) || !slippery.SlipData.SuperSlippery))
            args.NoSlip = true;
    }
}

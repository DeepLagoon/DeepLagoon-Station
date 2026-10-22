namespace Content.Shared._DeepLagoon.LustWish;

[RegisterComponent]
public sealed partial class LustWishVendorComponent : Component
{
    [DataField] public bool FreeVending;
    [DataField] public bool OriginalRequiresCash = true;
}

[RegisterComponent]
public sealed partial class LustWishDiscountCardComponent : Component;

[RegisterComponent]
public sealed partial class LustWishArousalItemComponent : Component
{
    [DataField] public float Amount = 5;
}

[RegisterComponent]
public sealed partial class LustWishPortalKitComponent : Component
{
    [DataField] public EntityUid? FirstPortal;
    [DataField] public bool Completed;
}

[RegisterComponent]
public sealed partial class LustWishPortalComponent : Component
{
    [DataField] public EntityUid? Peer;
    [ViewVariables] public EntityUid? Participant;
}

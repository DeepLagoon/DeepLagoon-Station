using Robust.Shared.GameStates;

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

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class LustWishPortalComponent : Component
{
    [DataField, AutoNetworkedField] public EntityUid? Peer;
    [DataField, AutoNetworkedField] public EntityUid? Support;
    [DataField, AutoNetworkedField] public EntityUid? Participant;
}

[RegisterComponent]
public sealed partial class LustWishPortalSupportComponent : Component
{
    [DataField] public HashSet<EntityUid> Portals = new();
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class LustWishPortalOccupantComponent : Component
{
    [DataField, AutoNetworkedField] public EntityUid Entrance;
    [DataField, AutoNetworkedField] public EntityUid Exit;
    [DataField, AutoNetworkedField] public EntityUid Projection;
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class LustWishProjectionComponent : Component
{
    [DataField, AutoNetworkedField] public EntityUid Body;
    [DataField, AutoNetworkedField] public EntityUid Portal;
}

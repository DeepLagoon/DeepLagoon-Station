using Content.Shared.Containers.ItemSlots;

namespace Content.Server._DeepLagoon.ShuttleParking;

[RegisterComponent]
public sealed partial class ParkingConsoleComponent : Component
{
    public const string IdSlot = "parking-id";
    [DataField] public ItemSlot Card = new();
}

/// <summary>Enables automatic parking for player shuttles docked directly to this station.</summary>
[RegisterComponent]
public sealed partial class StationAutomaticShuttleParkingComponent : Component;

/// <summary>Authoritative identity, never inferred from names or serialized runtime entity IDs.</summary>
[RegisterComponent]
public sealed partial class ParkingShuttleComponent : Component
{
    [DataField] public Guid Identity;
    [DataField("owner")] public Guid OwnerAccount;
    [DataField] public int OriginalPrice;
    [DataField] public string Vessel = "";
    [DataField] public string OwnerName = "";
    [DataField] public bool Parked;
    [DataField] public Dictionary<Guid, EntityUid> OriginalItems = [];
    [DataField] public Dictionary<EntityUid, EntityUid?>? ResearchConnections;
    // Same-round membership is deliberately not serialized into a persistent snapshot.
    public EntityUid? ReturnStation;
    public int ParkingCharacter = -1;
    public bool AutoParkingNoticePending;
}

[RegisterComponent]
public sealed partial class ParkingOriginalComponent : Component
{
    [DataField] public Guid Shuttle;
    [DataField] public Guid Identity;
}

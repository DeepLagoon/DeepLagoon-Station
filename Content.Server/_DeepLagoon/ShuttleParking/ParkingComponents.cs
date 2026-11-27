using Content.Shared.Containers.ItemSlots;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

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

/// <summary>Private restore/appraisal allocation; never a deployed ship or an active fleet reservation.</summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class ParkingStagingSnapshotComponent : Component;

/// <summary>Authoritative identity, never inferred from names or serialized runtime entity IDs.</summary>
[RegisterComponent]
public sealed partial class ParkingShuttleComponent : Component
{
    [DataField] public Guid Identity;
    [DataField("owner")] public Guid OwnerAccount;
    [DataField] public int OriginalPrice;
    [DataField] public string Vessel = "";
    [DataField] public string OwnerName = "";
    /// <summary>Purchase provenance survives deed transfers and never follows the vessel prototype or price.</summary>
    [DataField] public bool PurchasedWithVoucher;
    /// <summary>Canonical purchase-time JobPrototype.AssignedCompany; null means legacy provenance is unknown.</summary>
    [DataField] public string? OriginFaction;
    [DataField] public bool Parked;
    [DataField] public Dictionary<Guid, EntityUid> OriginalItems = [];
    [DataField] public bool EquipmentBaselineRecorded;
    [DataField] public string? OriginalShieldPrototype;
    [DataField] public Dictionary<EntityUid, EntityUid?>? ResearchConnections;
    // Station-level expedition policy otherwise lives outside the saved grid.
    // Null identifies older snapshots; an explicit false must remain disabled.
    [DataField] public bool? ExpeditionsEnabled;
    [DataField] public bool ExpeditionCooldown;
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))] public TimeSpan ExpeditionNextOffer;
    // Same-round membership is deliberately not serialized into a persistent snapshot.
    public EntityUid? ReturnStation;
    public int ParkingCharacter = -1;
    [DataField] public int ParkingProfile;
    public bool AutoParkingNoticePending;
}

[RegisterComponent]
public sealed partial class ParkingOriginalComponent : Component
{
    [DataField] public Guid Shuttle;
    [DataField] public Guid Identity;
    /// <summary>Purchase-time baseline; null identifies legacy snapshots with unknown provenance.</summary>
    [DataField] public string? BaselinePrototype;
}

/// <summary>Durable deed identity; runtime grid UIDs do not survive a server process.</summary>
[RegisterComponent]
public sealed partial class ParkingDeedIdentityComponent : Component
{
    [DataField] public Guid Identity;
    [DataField] public Guid Owner;
    [DataField] public int Profile;
}

using Content.Shared.Actions;
using Content.Shared.DoAfter;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;

namespace Content.Shared._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunConsoleComponent : Component;

[RegisterComponent]
public sealed partial class BitrunServerComponent : Component
{
    [DataField]
    public int Credits;
}

[RegisterComponent]
public sealed partial class BitrunPodComponent : Component
{
    [DataField]
    public string Loadout = "explorer";
    public EntProtoId? Program;
}

[RegisterComponent]
public sealed partial class BitrunProgramDiskComponent : Component
{
    [DataField]
    public EntProtoId? Item;

    [DataField]
    public EntProtoId? Action;
}

[RegisterComponent]
public sealed partial class BitrunForgeComponent : Component;

[RegisterComponent]
public sealed partial class BitrunVendorComponent : Component;

[RegisterComponent]
public sealed partial class BitrunAvatarSpawnComponent : Component;

[RegisterComponent]
public sealed partial class BitrunCacheSpawnComponent : Component;

[RegisterComponent]
public sealed partial class BitrunCacheGoalComponent : Component;

[RegisterComponent]
public sealed partial class BitrunExitComponent : Component;

[RegisterComponent]
public sealed partial class BitrunScenarioMarkerComponent : Component;

[Prototype]
public sealed partial class BitrunDomainPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public LocId Name;

    [DataField(required: true)]
    public LocId Description;

    [DataField(required: true)]
    public ResPath Map;

    [DataField]
    public int Difficulty;

    [DataField]
    public int Cost;

    [DataField]
    public int Credits = 1;

    [DataField]
    public int Reward;

    [DataField]
    public bool TestOnly;

    [DataField]
    public BitrunObjective Objective;

    [DataField]
    public int ObjectiveCount = 1;

    [DataField]
    public ProtoId<StartingGearPrototype>? Gear;

    [DataField]
    public bool RestrictLoadout;

    [DataField]
    public EntProtoId[] Loot = [];

    [DataField]
    public bool EchoVision;
}

public enum BitrunObjective : byte
{
    Cache,
    Boss,
    Hunt,
    Drinks,
    Fishing,
    Ritual,
    Island,
    Stages,
}

[Prototype]
public sealed partial class BitrunShopPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public EntProtoId Product;

    [DataField(required: true)]
    public int Price;
}

[Serializable, NetSerializable]
public enum BitrunUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum BitrunUiAction : byte
{
    Start,
    Random,
    Stop,
    BindResearch,
    SelectLoadout,
    Buy,
    Enter,
    Eject,
    RefreshVendorBalance,
}

[Serializable, NetSerializable]
public enum BitrunUiMode : byte
{
    Console,
    Pod,
    Vendor,
}

[Serializable, NetSerializable]
public enum BitrunSide : byte
{
    Attackers,
    Defenders,
}

[Serializable, NetSerializable]
public enum BitrunMachineVisuals : byte
{
    Active,
    Occupied,
}

[Serializable, NetSerializable]
public sealed record BitrunDomainEntry(string Id, string Name, string Description, int Difficulty, int Cost, int Reward);

[Serializable, NetSerializable]
public sealed record BitrunResearchEntry(NetEntity Entity, string Name);

[Serializable, NetSerializable]
public sealed record BitrunShopEntry(string Id, string Name, int Price);

[Serializable, NetSerializable]
public sealed record BitrunSessionEntry(Guid Id, NetEntity Server, string Domain, bool Waiting,
    TimeSpan Deadline, int Attackers, int Defenders, bool CanJoinAttackers, bool CanJoinDefenders);

[Serializable, NetSerializable]
public sealed class BitrunUiState : BoundUserInterfaceState
{
    public BitrunUiMode Mode;
    public BitrunDomainEntry[] Domains = [];
    public BitrunResearchEntry[] ResearchServers = [];
    public BitrunShopEntry[] ShopItems = [];
    public BitrunSessionEntry[] Sessions = [];
    public string[] Loadouts = [];
    public string? SelectedLoadout;
    public string? DomainId;
    public NetEntity? ResearchServer;
    public BitrunSide? OccupiedSide;
    public NetEntity? PreparingBody;
    public int Credits;
    public string? Status;
    public bool CanStart;
    public bool CanStop;
}

[Serializable, NetSerializable]
public sealed class BitrunUiMessage(BitrunUiAction action, string? id = null, NetEntity? target = null,
    BitrunSide? side = null, Guid? session = null) : BoundUserInterfaceMessage
{
    public readonly BitrunUiAction Action = action;
    public readonly string? Id = id;
    public readonly NetEntity? Target = target;
    public readonly BitrunSide? Side = side;
    public readonly Guid? Session = session;
}

public sealed partial class BitrunDisconnectEvent : InstantActionEvent;

[Serializable, NetSerializable]
public sealed class BitrunBalanceMessage(int credits, int researchPoints) : BoundUserInterfaceMessage
{
    public readonly int Credits = credits;
    public readonly int ResearchPoints = researchPoints;
}


[Serializable, NetSerializable]
public sealed partial class BitrunScenarioDoAfterEvent : SimpleDoAfterEvent;

[Serializable, NetSerializable]
public sealed partial class BitrunEjectDoAfterEvent : SimpleDoAfterEvent
{
    public readonly NetEntity Occupant;

    public BitrunEjectDoAfterEvent(NetEntity occupant)
    {
        Occupant = occupant;
    }
}

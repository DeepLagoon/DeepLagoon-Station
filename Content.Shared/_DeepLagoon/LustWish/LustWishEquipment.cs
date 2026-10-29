using Content.Shared.DoAfter;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;
using Content.Shared.Actions;

namespace Content.Shared._DeepLagoon.LustWish;

[RegisterComponent]
public sealed partial class LustWishAccessoryComponent : Component
{
    [DataField] public string Slot = "neck";
    [DataField] public string[] InventorySlots = [];
    [DataField] public bool Lockable = true;
    [DataField] public bool RestrictHands;
    [DataField] public bool RestrictLegs;
    [DataField] public bool Blind;
    [DataField] public bool Deaf;
    [DataField] public bool Hypnosis;
    [DataField] public bool Electronic;
    [DataField] public bool Shock;
    [DataField] public bool Active;
    [DataField] public int Intensity = 1;
    [DataField] public bool Locked;
    [DataField] public Guid KeyCode;
    [DataField] public EntityUid? Wearer;
    [DataField] public EntityUid? ActiveController;
    [DataField] public SpriteSpecifier? WornSprite;
    [ViewVariables] public EntityUid? LeashHolder;
    [ViewVariables] public EntityUid? Leash;
    [ViewVariables] public TimeSpan NextPulse;
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class LustWishPaletteComponent : Component
{
    [DataField, AutoNetworkedField] public Color Color = Color.White;
    [DataField, AutoNetworkedField] public EntityUid? ColorAction;
}

public sealed partial class LustWishChooseColorEvent : InstantActionEvent;

[Serializable, NetSerializable]
public sealed class LustWishColorPickerEvent(NetEntity item, string name, Color color) : EntityEventArgs
{
    public NetEntity Item = item;
    public string Name = name;
    public Color Color = color;
}

[Serializable, NetSerializable]
public sealed class LustWishSetColorEvent(NetEntity item, string color) : EntityEventArgs
{
    public NetEntity Item = item;
    public string Color = color;
}

[RegisterComponent]
public sealed partial class LustWishKeyComponent : Component
{
    [DataField] public Guid Code;
}

[RegisterComponent]
public sealed partial class LustWishRemoteComponent : Component
{
    [DataField] public EntityUid? Device;
    [DataField] public float Range = 32;
}

[RegisterComponent]
public sealed partial class LustWishLeashComponent : Component;

[RegisterComponent]
public sealed partial class LustWishPackComponent : Component
{
    [DataField(required: true)] public EntProtoId Contents;
    [DataField] public int Count = 4;
}

[RegisterComponent]
public sealed partial class LustWishWipeComponent : Component
{
    [DataField] public bool Used;
}

[RegisterComponent]
public sealed partial class LustWishFurnitureKitComponent : Component
{
    [DataField(required: true)] public EntProtoId Furniture;
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class LustWishWornEffectsComponent : Component
{
    [DataField, AutoNetworkedField] public bool HandsBlocked;
    [DataField, AutoNetworkedField] public bool LegsRestricted;
    [DataField, AutoNetworkedField] public bool Blind;
    [DataField, AutoNetworkedField] public bool Deaf;
    [DataField, AutoNetworkedField] public bool Focused;
    [DataField, AutoNetworkedField] public NetEntity? LeashHolder;
    [DataField, AutoNetworkedField] public Dictionary<string, NetEntity> Items = new();
}

[Serializable, NetSerializable]
public sealed record LustWishGearEntry(string Slot, string Name, bool Locked, bool Active, bool Electronic,
    bool Shock, bool Hypnosis, int Intensity, bool Colorable, bool Leashed);

[Serializable, NetSerializable]
public sealed class LustWishGearStateEvent(NetEntity target, LustWishGearEntry[] entries, string heldName, string heldSlot) : EntityEventArgs
{
    public NetEntity Target = target;
    public LustWishGearEntry[] Entries = entries;
    public string HeldName = heldName;
    public string HeldSlot = heldSlot;
}

[Serializable, NetSerializable]
public sealed class LustWishGearRequestEvent(NetEntity target, string operation, string slot = "", string value = "") : EntityEventArgs
{
    public NetEntity Target = target;
    public string Operation = operation;
    public string Slot = slot;
    public string Value = value;
}

[Serializable, NetSerializable]
public sealed partial class LustWishEscapeEvent : SimpleDoAfterEvent;

[Serializable, NetSerializable]
public sealed partial class LustWishBuildEvent : SimpleDoAfterEvent;

public static class LustWishEquipment
{
    public static readonly string[] Slots = ["neck", "wrists", "ankles", "hands", "head", "eyes", "ears", "torso", "penis"];
    public static readonly string[] Colors = ["#FFFFFF", "#AC70D4", "#E978B4", "#3EA5C3", "#CF4141", "#587C48", "#303039"];
    public const string ContainerPrefix = "lustwish-accessory-";
}

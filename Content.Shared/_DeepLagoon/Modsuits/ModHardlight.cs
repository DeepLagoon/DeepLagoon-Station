using Content.Shared.Humanoid;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Modsuits;

[Flags, Serializable, NetSerializable]
public enum ModHardlightParts : ushort
{
    None = 0,
    Head = 1 << 0,
    Chest = 1 << 1,
    LeftArm = 1 << 2,
    RightArm = 1 << 3,
    LeftLeg = 1 << 4,
    RightLeg = 1 << 5,
    Hair = 1 << 6,
    Ears = 1 << 7,
    Snout = 1 << 8,
    Horns = 1 << 9,
    Tail = 1 << 10,
    Wings = 1 << 11,
    Antennae = 1 << 12,
    Spines = 1 << 13,
    Taur = 1 << 14,
    All = (1 << 15) - 1,
}

[Serializable, NetSerializable]
public enum ModHardlightEffect : byte
{
    Base,
    Static,
    Scanline,
    Wave,
    Field,
    Hieroglyphs,
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ModHardlightVisualComponent : Component
{
    [AutoNetworkedField]
    public ModHardlightParts Parts;

    [AutoNetworkedField]
    public Color Color;

    [AutoNetworkedField]
    public ModHardlightEffect Effect;
}

[Serializable, NetSerializable]
public enum ModHardlightKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class ModHardlightState(Color color, ModHardlightEffect effect, ModHardlightParts parts) : BoundUserInterfaceState
{
    public readonly Color Color = color;
    public readonly ModHardlightParts Parts = parts;
    public readonly ModHardlightEffect Effect = effect;
}

[Serializable, NetSerializable]
public sealed class ModHardlightStyleMessage(string color, ModHardlightEffect effect) : BoundUserInterfaceMessage
{
    public readonly string Color = color;
    public readonly ModHardlightEffect Effect = effect;
}

public static class ModHardlightRegions
{
    public static readonly (ModHardlightParts Part, string Id, string Slot, string Icon)[] Options =
    [
        (ModHardlightParts.Head, "head", "head", "helmet"),
        (ModHardlightParts.Chest, "chest", "outerClothing", "chestplate"),
        (ModHardlightParts.LeftArm, "left-arm", "gloves", "gauntlets"),
        (ModHardlightParts.RightArm, "right-arm", "gloves", "gauntlets"),
        (ModHardlightParts.LeftLeg, "left-leg", "shoes", "boots"),
        (ModHardlightParts.RightLeg, "right-leg", "shoes", "boots"),
        (ModHardlightParts.Hair, "hair", "head", "open"),
        (ModHardlightParts.Ears, "ears", "head", "ears"),
        (ModHardlightParts.Snout, "snout", "head", "snout"),
        (ModHardlightParts.Horns, "horns", "head", "horns"),
        (ModHardlightParts.Tail, "tail", "outerClothing", "tail"),
        (ModHardlightParts.Wings, "wings", "outerClothing", "some_wings"),
        (ModHardlightParts.Antennae, "antennae", "head", "ipc_antenna"),
        (ModHardlightParts.Spines, "spines", "outerClothing", "spines"),
        (ModHardlightParts.Taur, "taur", "outerClothing", "taur"),
    ];

    public static ModHardlightParts FromLayer(HumanoidVisualLayers layer)
    {
        return layer switch
        {
            HumanoidVisualLayers.Head or HumanoidVisualLayers.Eyes => ModHardlightParts.Head,
            HumanoidVisualLayers.Chest => ModHardlightParts.Chest,
            HumanoidVisualLayers.LArm or HumanoidVisualLayers.LHand => ModHardlightParts.LeftArm,
            HumanoidVisualLayers.RArm or HumanoidVisualLayers.RHand => ModHardlightParts.RightArm,
            HumanoidVisualLayers.LLeg or HumanoidVisualLayers.LFoot => ModHardlightParts.LeftLeg,
            HumanoidVisualLayers.RLeg or HumanoidVisualLayers.RFoot => ModHardlightParts.RightLeg,
            HumanoidVisualLayers.Hair or HumanoidVisualLayers.FacialHair => ModHardlightParts.Hair,
            HumanoidVisualLayers.HeadSide or HumanoidVisualLayers.Special => ModHardlightParts.Ears,
            HumanoidVisualLayers.HeadTop => ModHardlightParts.Ears | ModHardlightParts.Horns,
            HumanoidVisualLayers.Snout => ModHardlightParts.Snout,
            HumanoidVisualLayers.Tail or HumanoidVisualLayers.TailExtras => ModHardlightParts.Tail,
            HumanoidVisualLayers.Wings or HumanoidVisualLayers.LArmExtension or HumanoidVisualLayers.RArmExtension => ModHardlightParts.Wings,
            _ => ModHardlightParts.None,
        };
    }
}

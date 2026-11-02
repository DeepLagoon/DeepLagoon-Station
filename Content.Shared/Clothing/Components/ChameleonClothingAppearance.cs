using Content.Shared.Hands.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Clothing.Components;

/// <summary>A cosmetic snapshot of worn equipment; contains no access, protection or storage properties.</summary>
[Serializable, NetSerializable, DataDefinition]
public sealed partial class ChameleonClothingAppearance
{
    [DataField] public EntProtoId Prototype;
    [DataField] public bool Folded;
    [DataField] public Color? PaletteColor;
    [DataField] public Dictionary<string, (string State, Color? Color)> RandomSprites = new();
    [DataField] public string? ClothingRsi;
    [DataField] public string? EquippedPrefix;
    [DataField] public string? EquippedState;
    [DataField] public Dictionary<string, List<PrototypeLayerData>> ClothingVisuals = new();
    [DataField] public byte BodyFit;
    [DataField] public bool FaceFit;
    [DataField] public bool HairOcclusion;
    [DataField] public bool PreserveLegHem;
    [DataField] public bool HidesUniform;
    [DataField] public string? MappedLayer;
    [DataField] public string? ItemRsi;
    [DataField] public string? HeldPrefix;
    [DataField] public Dictionary<HandLocation, List<PrototypeLayerData>> InhandVisuals = new();
}

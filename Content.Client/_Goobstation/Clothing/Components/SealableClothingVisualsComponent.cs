namespace Content.Client._Goobstation.Clothing.Components;

[RegisterComponent]
public sealed partial class SealableClothingVisualsComponent : Component
{
    [DataField]
    public string SpriteLayer = "sealed";

    /// <summary>Uses complete sealed sprites instead of adding a sealed overlay.</summary>
    [DataField]
    public bool ReplaceBaseLayers;

    [DataField]
    public Dictionary<string, List<PrototypeLayerData>> ClothingVisuals = new(); //just use ClothingVisuals like anything else
}

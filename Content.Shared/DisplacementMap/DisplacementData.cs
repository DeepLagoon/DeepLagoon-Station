namespace Content.Shared.DisplacementMap;

[DataDefinition]
public sealed partial class DisplacementData
{
    /// <summary>
    /// allows you to attach different maps for layers of different sizes.
    /// </summary>
    [DataField(required: true)]
    public Dictionary<int, PrototypeLayerData> SizeMaps = new();

    /// <summary>Alternative maps for garments whose lower fabric hangs between the legs.</summary>
    [DataField]
    public Dictionary<int, PrototypeLayerData> HemMaps = new();

    [DataField]
    public string? ShaderOverride = "DisplacedStencilDraw";
}

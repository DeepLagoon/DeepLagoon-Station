namespace Content.Server._DeepLagoon.Mapping;

/// <summary>Runtime-only provenance; map exports must never persist the server source filename.</summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class GridMapSourceComponent : Component
{
    public string Source = "";
    public bool FromContent;
}

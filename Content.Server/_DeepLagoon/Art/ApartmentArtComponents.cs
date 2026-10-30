namespace Content.Server._DeepLagoon.Art;

[RegisterComponent]
public sealed partial class ArtDurableCanvasComponent : Component;

/// <summary>Private apartment frame (also marks its uninstalled item).</summary>
[RegisterComponent]
public sealed partial class ApartmentPaintingFrameComponent : Component
{
    [DataField] public Guid Owner;
}

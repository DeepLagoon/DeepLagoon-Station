using Robust.Client.Graphics;
using Robust.Shared.Enums;

namespace Content.Client._DeepLagoon.Admin;

public sealed class AdminMapPlacementOverlay(AdminMapUploadSystem placement) : Overlay
{
    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    protected override void Draw(in OverlayDrawArgs args) => placement.DrawPlacement(in args);
}

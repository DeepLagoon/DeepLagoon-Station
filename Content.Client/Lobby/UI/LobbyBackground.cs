using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.Client.Lobby.UI;

/// <summary>
/// Lobby artwork that smoothly follows the cursor without moving the lobby controls.
/// </summary>
public sealed partial class LobbyBackground : TextureRect
{
    private const float Zoom = 1.06f;
    private const float Travel = 0.0125f;
    private const float Response = 6f;

    private Vector2 _cursorOffset;

    public LobbyBackground()
    {
        CanShrink = true;
        MouseFilter = MouseFilterMode.Ignore;
        RectClipContent = true;
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        UpdateMedia(args.DeltaSeconds);

        if (Texture == null || Size.X <= 0 || Size.Y <= 0)
        {
            _cursorOffset = Vector2.Zero;
            return;
        }

        // Use the global cursor so movement also works over buttons and the web chat.
        var cursor = UserInterfaceManager.MousePositionScaled.Position - GlobalPosition;
        var target = Vector2.Clamp(cursor / Size * 2f - Vector2.One, -Vector2.One, Vector2.One);
        var blend = 1f - MathF.Exp(-Response * args.DeltaSeconds);
        _cursorOffset = Vector2.Lerp(_cursorOffset, target, blend);
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        var texture = Texture;
        if (texture == null || PixelSize.X <= 0 || PixelSize.Y <= 0)
            return;

        var viewport = (Vector2) PixelSize;
        var textureSize = (Vector2) texture.Size;
        var scale = MathF.Max(viewport.X / textureSize.X, viewport.Y / textureSize.Y) * Zoom;
        var drawSize = textureSize * scale;
        var origin = (viewport - drawSize) / 2f + _cursorOffset * viewport * Travel;

        // Crop inside the texture instead of drawing beyond the background control.
        // The zoom leaves at least 3% on each edge for the 1.25% cursor travel.
        var region = new UIBox2(-origin / scale, (viewport - origin) / scale);
        handle.DrawTextureRectRegion(texture, PixelSizeBox, region);
    }
}

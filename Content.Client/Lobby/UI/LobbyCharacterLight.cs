using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Graphics;

namespace Content.Client.Lobby.UI;

/// <summary>Soft screen illumination behind the avatar, without tinting its sprite.</summary>
public sealed class LobbyCharacterLight : Control
{
    private readonly Vector2[] _ellipse = new Vector2[34];

    public LobbyCharacterLight()
    {
        MouseFilter = MouseFilterMode.Ignore;
        RectClipContent = true;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        for (var ring = 24; ring > 0; ring--)
        {
            var radius = ring / 24f;
            DrawEllipse(handle, new Vector2(PixelWidth / 2, PixelHeight * 0.55f),
                new Vector2(76, 88) * UIScale * radius, 0.003f);
            DrawEllipse(handle, new Vector2(PixelWidth / 2, PixelHeight * 0.82f),
                new Vector2(76, 15) * UIScale * radius, 0.006f);
        }
    }

    private void DrawEllipse(DrawingHandleScreen handle, Vector2 center, Vector2 radius, float alpha)
    {
        _ellipse[0] = center;
        for (var i = 0; i <= 32; i++)
        {
            var angle = MathF.Tau * i / 32;
            _ellipse[i + 1] = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        }
        handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, _ellipse, LobbyConsoleTheme.Green.WithAlpha(alpha));
    }
}

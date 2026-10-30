using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;

namespace Content.Client.Lobby.UI;

/// <summary>The small terminal status line above the screen's action grid.</summary>
public sealed class LobbyConsoleHeading : Control
{
    private readonly Font _font = LobbyConsoleTheme.GetFont(9);

    public LobbyConsoleHeading()
    {
        MouseFilter = MouseFilterMode.Ignore;
        RectClipContent = true;
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize) => new(0, 25);

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        handle.DrawRect(UIBox2.FromDimensions(0, 0, PixelWidth, 21 * UIScale), Color.FromHex("#14271B"));
        var ink = LobbyConsoleTheme.Green.WithAlpha(0.55f);
        handle.DrawRect(UIBox2.FromDimensions(3 * UIScale, 5 * UIScale, 4 * UIScale, 4 * UIScale), ink);
        handle.DrawString(_font, new Vector2(13, 0) * UIScale, "DEEPLAGOON / TERMINAL", UIScale, ink);
        if (Width >= 240)
        {
            var size = handle.GetDimensions(_font, "ONLINE", UIScale);
            handle.DrawString(_font, new Vector2(PixelWidth - size.X - 3 * UIScale, 0), "ONLINE", UIScale, ink);
        }
        handle.DrawRect(UIBox2.FromDimensions(0, 20 * UIScale, PixelWidth, UIScale), LobbyConsoleTheme.Green.WithAlpha(0.19f));
    }
}

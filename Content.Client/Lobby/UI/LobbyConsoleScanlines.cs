using Robust.Client.Graphics;
using Robust.Client.UserInterface;

namespace Content.Client.Lobby.UI;

/// <summary>Static CRT lines drawn above screen contents without intercepting input.</summary>
public sealed class LobbyConsoleScanlines : Control
{
    public LobbyConsoleScanlines()
    {
        MouseFilter = MouseFilterMode.Ignore;
        RectClipContent = true;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        var pitch = LobbyConsoleTheme.ScanlinePitch * UIScale;
        var color = Color.Black.WithAlpha(LobbyConsoleTheme.ScanlineOpacity);
        for (var y = pitch - UIScale; y < PixelHeight; y += pitch)
            handle.DrawRect(UIBox2.FromDimensions(0, y, PixelWidth, Math.Min(UIScale, PixelHeight - y)), color);
    }
}

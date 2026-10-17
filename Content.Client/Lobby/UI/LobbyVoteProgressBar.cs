using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.Lobby.UI;

/// <summary>A flat segmented screen indicator, with its fill clipped to the native vote range.</summary>
public sealed class LobbyVoteProgressBar : ProgressBar
{
    private bool _useLobbyStyle;

    public bool UseLobbyStyle
    {
        get => _useLobbyStyle;
        set
        {
            if (UseLobbyStyle == value)
                return;
            _useLobbyStyle = value;
            InvalidateMeasure();
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
        => UseLobbyStyle ? new Vector2(0, 16) : base.MeasureOverride(availableSize);

    protected override void Draw(DrawingHandleScreen handle)
    {
        if (!UseLobbyStyle)
        {
            base.Draw(handle);
            return;
        }

        handle.DrawRect(PixelSizeBox, LobbyConsoleTheme.Screen);
        handle.DrawRect(PixelSizeBox, LobbyConsoleTheme.Green.WithAlpha(0.10f));
        handle.DrawRect(PixelSizeBox, LobbyConsoleTheme.Green.WithAlpha(0.28f), false);
        var left = 4 * UIScale;
        var top = 4 * UIScale;
        var trackWidth = Math.Max(0, PixelWidth - 8 * UIScale);
        var trackHeight = Math.Max(0, PixelHeight - 8 * UIScale);
        var fillWidth = trackWidth * Math.Clamp(GetAsRatio(), 0, 1);
        for (var x = 0f; x < trackWidth; x += 7 * UIScale)
        {
            var segmentWidth = Math.Min(5 * UIScale, trackWidth - x);
            var litWidth = Math.Clamp(fillWidth - x, 0, segmentWidth);
            if (litWidth > 0)
                handle.DrawRect(UIBox2.FromDimensions(left + x, top, litWidth, trackHeight), LobbyConsoleTheme.Green);
            // Dark cuts remain visible across both the lit and unlit parts.
            var gap = Math.Min(2 * UIScale, trackWidth - x - segmentWidth);
            if (gap > 0)
                handle.DrawRect(UIBox2.FromDimensions(left + x + segmentWidth, top, gap, trackHeight), LobbyConsoleTheme.Screen);
        }
    }
}

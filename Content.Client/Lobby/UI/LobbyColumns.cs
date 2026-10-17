using System.Numerics;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.Lobby.UI;

/// <summary>
/// Places a collapsible character drawer beside the full-height chat, with utilities at the left.
/// </summary>
public sealed class LobbyColumns : Container
{
    private float _sidePanelWidth = 400;

    public float SidePanelWidth
    {
        get => _sidePanelWidth;
        set
        {
            if (_sidePanelWidth == value)
                return;
            _sidePanelWidth = value;
            InvalidateMeasure();
        }
    }

    private static float RightWidth(float width, float requested) => Math.Clamp(requested, 0, width * 0.4f);

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var right = RightWidth(availableSize.X, SidePanelWidth);
        if (ChildCount == 3)
        {
            GetChild(2).Measure(new Vector2(right, availableSize.Y));
            GetChild(1).Measure(new Vector2(Math.Max(0, availableSize.X - right - 16), availableSize.Y));
            var drawer = GetChild(1).DesiredSize.X;
            var left = Math.Min(LobbyConsoleTheme.SidebarWidth, Math.Max(0, availableSize.X - right - drawer - 32));
            GetChild(0).Measure(new Vector2(left, availableSize.Y));
        }
        // Content length must not expand either sidebar across the background.
        return Vector2.Zero;
    }

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        var right = RightWidth(finalSize.X, SidePanelWidth);
        if (ChildCount == 3)
        {
            var drawer = Math.Min(GetChild(1).DesiredSize.X, Math.Max(0, finalSize.X - right - 16));
            var drawerX = finalSize.X - right - 16 - drawer;
            var left = Math.Min(LobbyConsoleTheme.SidebarWidth, Math.Max(0, drawerX - 16));
            GetChild(0).Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(left, finalSize.Y)));
            GetChild(1).Arrange(UIBox2.FromDimensions(new Vector2(drawerX, 0), new Vector2(drawer, finalSize.Y)));
            GetChild(2).Arrange(UIBox2.FromDimensions(new Vector2(finalSize.X - right, 0), new Vector2(right, finalSize.Y)));
        }
        return finalSize;
    }
}

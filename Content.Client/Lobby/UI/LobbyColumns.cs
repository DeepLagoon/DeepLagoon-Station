using System.Numerics;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.Lobby.UI;

/// <summary>
/// Places character and music controls at the left, with collapsible utilities beside the full-height chat.
/// </summary>
public sealed class LobbyColumns : Container
{
    private float _sidePanelWidth = 400;
    private const float Gap = 16;
    private const float MinimumLeftWidth = LobbyButtonPanel.LargeWidth + 16;

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

    private static float RightWidth(float width, float requested) => Math.Clamp(requested, 0,
        Math.Min(width * 0.4f, Math.Max(0, width - MinimumLeftWidth - LobbyUtilityDrawer.MinimumExpandedWidth - 2 * Gap)));

    private static float LeftWidth(float width, float right) => Math.Min(LobbyConsoleTheme.SidebarWidth,
        Math.Max(0, width - right - LobbyUtilityDrawer.MinimumExpandedWidth - 2 * Gap));

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var right = RightWidth(availableSize.X, SidePanelWidth);
        if (ChildCount == 3)
        {
            GetChild(2).Measure(new Vector2(right, availableSize.Y));
            var left = LeftWidth(availableSize.X, right);
            GetChild(0).Measure(new Vector2(left, availableSize.Y));
            GetChild(1).Measure(new Vector2(Math.Max(0, availableSize.X - right - left - 2 * Gap), availableSize.Y));
        }
        // Content length must not expand either sidebar across the background.
        return Vector2.Zero;
    }

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        var right = RightWidth(finalSize.X, SidePanelWidth);
        if (ChildCount == 3)
        {
            var left = LeftWidth(finalSize.X, right);
            var drawer = Math.Min(GetChild(1).DesiredSize.X, Math.Max(0, finalSize.X - right - left - 2 * Gap));
            var drawerX = finalSize.X - right - Gap - drawer;
            GetChild(0).Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(left, finalSize.Y)));
            GetChild(1).Arrange(UIBox2.FromDimensions(new Vector2(drawerX, 0), new Vector2(drawer, finalSize.Y)));
            GetChild(2).Arrange(UIBox2.FromDimensions(new Vector2(finalSize.X - right, 0), new Vector2(right, finalSize.Y)));
        }
        return finalSize;
    }
}

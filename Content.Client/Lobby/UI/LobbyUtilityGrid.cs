using System.Linq;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.Lobby.UI;

/// <summary>Flat screen cells with shared separators, rather than repeated hardware frames.</summary>
public sealed class LobbyUtilityGrid : Container
{
    private static int Columns(float width) => width >= 180 ? 2 : 1;

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var children = Children.Where(child => child.Visible).ToArray();
        var availableWidth = float.IsFinite(availableSize.X) ? availableSize.X : LobbyConsoleTheme.SidebarWidth - 2 * LobbyConsoleTheme.ContentInset;
        var columns = Columns(availableWidth);
        var width = Math.Max(0, availableWidth / columns);
        foreach (var child in children)
            child.Measure(new Vector2(width, LobbyConsoleTheme.RowHeight));
        var rows = (children.Length + columns - 1) / columns;
        return new Vector2(availableWidth, rows * LobbyConsoleTheme.RowHeight);
    }

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        var columns = Columns(finalSize.X);
        var width = Math.Max(0, finalSize.X / columns);
        var index = 0;
        foreach (var child in Children.Where(child => child.Visible))
        {
            child.Arrange(UIBox2.FromDimensions(new Vector2(index % columns * width, index / columns * LobbyConsoleTheme.RowHeight), new Vector2(width, LobbyConsoleTheme.RowHeight)));
            index++;
        }
        return finalSize;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        var count = Children.Count(child => child.Visible);
        var columns = Columns(Width);
        var rows = (count + columns - 1) / columns;
        var ink = LobbyConsoleTheme.Green.WithAlpha(0.13f);
        if (columns == 2)
            handle.DrawRect(UIBox2.FromDimensions(PixelWidth / 2f, 0, UIScale, PixelHeight), ink);
        for (var row = 1; row < rows; row++)
            handle.DrawRect(UIBox2.FromDimensions(0, row * LobbyConsoleTheme.RowHeight * UIScale, PixelWidth, UIScale), ink);
    }
}

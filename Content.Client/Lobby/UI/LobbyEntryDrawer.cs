using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.Lobby.UI;

/// <summary>Character and entry controls fold toward chat without changing chat geometry.</summary>
public sealed class LobbyEntryDrawer : Container
{
    public const float BodyWidth = 340;
    public const float TabWidth = 32;
    public const float ExpandedWidth = BodyWidth + TabWidth + 4;
    public Button ToggleButton { get; }
    private bool _collapsed;

    public bool Collapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value)
                return;
            _collapsed = value;
            ApplyState();
            InvalidateMeasure();
            Parent?.InvalidateMeasure();
        }
    }

    public LobbyEntryDrawer()
    {
        RectClipContent = true;
        ToggleButton = new Button { Text = ">" };
        LobbyButtonArtwork.AttachUtility(ToggleButton, nativeText: true);
        ToggleButton.StyleBoxOverride = new StyleBoxFlat
        {
            BackgroundColor = LobbyConsoleTheme.Screen,
            BorderColor = LobbyConsoleTheme.Metal,
            BorderThickness = new Thickness(3),
        };
        ToggleButton.Label.Align = Label.AlignMode.Center;
        ToggleButton.Label.Margin = new Thickness(2);
        ToggleButton.OnPressed += _ => Collapsed = !Collapsed;
        AddChild(ToggleButton);
        ApplyState();
    }

    protected override void ChildAdded(Control child)
    {
        base.ChildAdded(child);
        if (child != ToggleButton)
        {
            child.SetPositionFirst();
            child.Visible = !Collapsed;
        }
    }

    private void ApplyState()
    {
        if (ChildCount == 2)
            GetChild(0).Visible = !Collapsed;
        ToggleButton.Text = Collapsed ? "<" : ">";
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (ChildCount == 2 && !Collapsed)
            GetChild(0).Measure(new Vector2(BodyWidth, availableSize.Y));
        ToggleButton.Measure(new Vector2(TabWidth, 56));
        return new Vector2(Collapsed ? TabWidth : ExpandedWidth, 0);
    }

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        if (ChildCount == 2 && !Collapsed)
        {
            var content = GetChild(0);
            var height = Math.Min(content.DesiredSize.Y, finalSize.Y);
            content.Arrange(UIBox2.FromDimensions(new Vector2(0, (finalSize.Y - height) / 2),
                new Vector2(BodyWidth, height)));
        }
        ToggleButton.Arrange(UIBox2.FromDimensions(new Vector2(finalSize.X - TabWidth, Math.Max(0, (finalSize.Y - 56) / 2)),
            new Vector2(TabWidth, Math.Min(56, finalSize.Y))));
        return finalSize;
    }
}

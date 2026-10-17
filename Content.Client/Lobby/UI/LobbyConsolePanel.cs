using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.Lobby.UI;

/// <summary>A resizable gray console enclosure; live server text remains native rich text.</summary>
[Virtual]
public class LobbyConsolePanel : PanelContainer
{
    private readonly LobbyConsoleScanlines _scanlines = new();
    private bool _useConsoleStyle;

    public bool UseConsoleStyle
    {
        get => _useConsoleStyle;
        set
        {
            _useConsoleStyle = value;
            PanelOverride = value ? CreateFrame() : null;
            // AngleRect's legacy stylesheet tint also affects PanelOverride.
            // Console artwork already contains its final metal/screen palette.
            ModulateSelfOverride = value ? Color.White : null;
            _scanlines.Visible = value && DrawScanlines;
            InvalidateMeasure();
        }
    }

    public bool DrawScanlines { get; set; } = true;

    public LobbyConsolePanel()
    {
        AddChild(_scanlines);
        UseConsoleStyle = true;
    }

    protected override void ChildAdded(Control newChild)
    {
        base.ChildAdded(newChild);
        // XAML adds content after our constructor. Keep the non-interactive CRT
        // layer above all text and buttons, just like the template's ::after.
        _scanlines.SetPositionLast();
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var padding = GetStyleBox()?.MinimumSize ?? Vector2.Zero;
        var content = Vector2.Zero;
        foreach (var child in Children)
        {
            if (child == _scanlines)
                continue;
            child.Measure(Vector2.Max(availableSize - padding, Vector2.Zero));
            content = Vector2.Max(content, child.DesiredSize);
        }
        return padding + content;
    }

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        base.ArrangeOverride(finalSize);
        var inset = new Vector2(LobbyConsoleTheme.ScreenInset);
        _scanlines.Visible = UseConsoleStyle && DrawScanlines;
        _scanlines.Arrange(UIBox2.FromDimensions(inset, Vector2.Max(finalSize - 2 * inset, Vector2.Zero)));
        return finalSize;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        if (!UseConsoleStyle)
            return;
        var inset = new Vector2(LobbyConsoleTheme.ScreenInset * UIScale);
        var size = Vector2.Max(PixelSize - 2 * inset, Vector2.Zero);
        handle.DrawRect(UIBox2.FromDimensions(inset, size), LobbyConsoleTheme.Screen);
        // The template adds a subtle gray inner lip to the original housing.
        for (var lip = 4; lip < 7; lip++)
            handle.DrawRect(UIBox2.FromDimensions(new Vector2(lip * UIScale),
                Vector2.Max(PixelSize - new Vector2(2 * lip * UIScale), Vector2.Zero)), LobbyConsoleTheme.Metal.WithAlpha(0.22f), false);
        for (var depth = 0; depth < 14; depth++)
        {
            var edge = new Vector2(depth * UIScale);
            if (size.X <= edge.X * 2 || size.Y <= edge.Y * 2)
                break;
            handle.DrawRect(UIBox2.FromDimensions(inset + edge, size - 2 * edge), Color.Black.WithAlpha(0.08f * (1 - depth / 14f)), false);
        }
    }

    public static StyleBoxTexture CreateFrame(float horizontal = LobbyConsoleTheme.ContentInset,
        float top = LobbyConsoleTheme.ContentInset, float bottom = LobbyConsoleTheme.ContentInset)
    {
        var frame = new StyleBoxTexture
        {
            Texture = IoCManager.Resolve<IResourceCache>().GetResource<TextureResource>("/Textures/_DeepLagoon/Interface/Lobby/console-frame.png"),
        };
        frame.SetPatchMargin(StyleBox.Margin.All, 16);
        frame.SetContentMarginOverride(StyleBox.Margin.Horizontal, horizontal);
        frame.SetContentMarginOverride(StyleBox.Margin.Top, top);
        frame.SetContentMarginOverride(StyleBox.Margin.Bottom, bottom);
        return frame;
    }
}

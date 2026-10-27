using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;

namespace Content.Client.Lobby.UI;

/// <summary>Keep the browser in one parent; tabs only select the content under it.</summary>
public sealed class TguiEditorHost : Container
{
    private readonly TabContainer _tabs;
    private readonly TguiPanel _panel;
    private readonly Control _sprite;
    private Vector2? _previewPosition;
    private Vector2 _previewSize;
    public bool MarkingsLayout { get; set; }

    public void SetPreviewBounds(Vector2 position, Vector2 size)
    {
        _previewPosition = position;
        _previewSize = size;
        InvalidateArrange();
    }

    public TguiEditorHost(TabContainer tabs, TguiPanel panel, Control sprite)
    {
        HorizontalExpand = VerticalExpand = true;
        _tabs = tabs;
        _panel = panel;
        _sprite = sprite;
        AddChild(tabs);
        AddChild(panel);
        AddChild(sprite);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        _tabs.Measure(availableSize);
        _panel.Measure(availableSize);
        _sprite.Measure(new Vector2(232, 320));
        // Specialized native tabs may have large minimum sizes. The editor is
        // a viewport: those sizes must not resize the lobby/chat when switching.
        return Vector2.Zero;
    }

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        _tabs.Arrange(UIBox2.FromDimensions(Vector2.Zero, finalSize));
        _panel.Arrange(UIBox2.FromDimensions(Vector2.Zero, finalSize));
        // Match CharacterEditor.scss: small viewports give all space to the
        // form, keeping the native sprite from covering browser controls.
        if (MarkingsLayout)
        {
            _sprite.Visible = _previewPosition is { } origin && origin.Y >= 52 && origin.Y + _previewSize.Y <= finalSize.Y + 1;
            if (_sprite.Visible && _previewPosition is { } position)
            {
                var size = Vector2.Min(_previewSize, finalSize);
                _sprite.SetSize = size;
                _sprite.Margin = new Thickness(0);
                if (_sprite is SpriteView view)
                    view.Scale = new Vector2(Math.Clamp(Math.Min(size.X / 40, size.Y / 48), 2, 7));
                _sprite.Arrange(UIBox2.FromDimensions(position, size));
            }
            return finalSize;
        }
        _previewPosition = null;
        _sprite.SetSize = new Vector2(232, 320);
        _sprite.Margin = new Thickness(4);
        if (_sprite is SpriteView normalView) normalView.Scale = new Vector2(7);
        _sprite.Visible = finalSize.X >= 700 && finalSize.Y >= 520;
        if (_sprite.Visible)
            _sprite.Arrange(UIBox2.FromDimensions(new Vector2(78, 62 + 48), new Vector2(232, 320)));
        return finalSize;
    }
}

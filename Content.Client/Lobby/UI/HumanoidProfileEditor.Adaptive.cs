using System.Numerics;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    protected override void Resized()
    {
        base.Resized();
        // TGUI owns the responsive layout. Do not resize hidden native pickers
        // or switch the parent orientation as the browser viewport shrinks.
        if (_tguiHost != null && _profileWeb?.UsingNative == false)
            return;
        var stacked = Size.X < 580f;
        Orientation = stacked ? LayoutOrientation.Vertical : LayoutOrientation.Horizontal;
        EditorColumn.VerticalExpand = true;
        PreviewColumn.MaxHeight = stacked ? Math.Max(180f, Size.Y * 0.40f) : float.PositiveInfinity;
        var previewWidth = stacked ? Math.Max(280f, Size.X - 16f) : Math.Clamp(Size.X * 0.30f, 280f, 400f);
        PreviewColumn.MinWidth = previewWidth;
        PreviewColumn.MaxWidth = previewWidth;
        var spriteWidth = Math.Max(100f, previewWidth - 168f);
        SpriteView.SetSize = new Vector2(spriteWidth, 280f);
        SpriteView.Scale = new Vector2(Math.Clamp(spriteWidth / 32f, 3f, 7f));
        var contentWidth = Math.Max(210f, stacked ? Size.X - 56f : Size.X - previewWidth - 72f);
        var sideBySide = contentWidth >= 600f;
        HairPickers.Orientation = sideBySide ? BoxContainer.LayoutOrientation.Horizontal : BoxContainer.LayoutOrientation.Vertical;
        var pickerWidth = sideBySide ? (contentWidth - 8f) / 2f : contentWidth;
        HairStylePicker.SetWidth = pickerWidth;
        FacialHairPicker.SetWidth = pickerWidth;
    }
}

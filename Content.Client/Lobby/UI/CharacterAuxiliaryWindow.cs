using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Timing;

namespace Content.Client.Lobby.UI;

/// <summary>A content-owned floating browser window without a native title bar.</summary>
public class CharacterAuxiliaryWindow : BaseWindow
{
    public readonly TguiPanel Panel = new(inheritChatAppearance: false, forceTgui: true);

    public CharacterAuxiliaryWindow()
    {
        MouseFilter = MouseFilterMode.Stop;
        Panel.Margin = new Thickness(8);
        AddChild(Panel);
        MinSize = new Vector2(360, 240);
        SetSize = new Vector2(680, 600);
    }

    protected override void Draw(DrawingHandleScreen handle) => CharacterWindowFrame.Draw(handle, PixelSize, UIScale, Resizable);

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (Parent == null) return;
        var minSize = Vector2.Min(new Vector2(360, 240), Parent.Size);
        if (MinSize != minSize) MinSize = minSize;
        if (Size.X > Parent.Size.X || Size.Y > Parent.Size.Y) SetSize = Vector2.Min(Size, Parent.Size);
        var position = Vector2.Clamp(Position, Vector2.Zero, Vector2.Max(Vector2.Zero, Parent.Size - Size));
        if (Position != position) LayoutContainer.SetPosition(this, position);
    }

    protected override DragMode GetDragModeFor(Vector2 pointer)
    {
        if (!Resizable) return DragMode.None;
        var mode = DragMode.None;
        if (pointer.X < 8) mode |= DragMode.Left;
        else if (pointer.X > Size.X - 8) mode |= DragMode.Right;
        if (pointer.Y < 8) mode |= DragMode.Top;
        else if (pointer.Y > Size.Y - 8) mode |= DragMode.Bottom;
        return mode;
    }
}

internal static class CharacterWindowFrame
{
    public static void Draw(DrawingHandleScreen handle, Vector2 size, float scale, bool resizable)
    {
        // Newly opened auxiliary windows can be drawn before their first layout pass.
        if (size.X <= 0 || size.Y <= 0) return;
        handle.DrawRect(new UIBox2(Vector2.Zero, size), Color.FromHex("#35485C"));
        if (size.X > 4 * scale && size.Y > 4 * scale)
            handle.DrawRect(new UIBox2(new Vector2(2 * scale), size - new Vector2(2 * scale)), Color.FromHex("#101923"));
        if (size.X > 10 * scale && size.Y > 10 * scale)
            handle.DrawRect(new UIBox2(new Vector2(5 * scale), size - new Vector2(5 * scale)), Color.FromHex("#70849B"), false);
        if (!resizable || size.X < 28 * scale || size.Y < 28 * scale) return;
        // Grip markings stay outside the browser and coincide with the native resize hit area.
        foreach (var offset in new[] { 2f, 4f, 6f })
        {
            handle.DrawRect(new UIBox2(size.X - 28 * scale, size.Y - offset * scale - scale,
                size.X - 10 * scale, size.Y - offset * scale), Color.FromHex("#A7C4DF"));
            handle.DrawRect(new UIBox2(size.X - offset * scale - scale, size.Y - 28 * scale,
                size.X - offset * scale, size.Y - 10 * scale), Color.FromHex("#A7C4DF"));
        }
    }

}

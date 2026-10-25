using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Timing;

namespace Content.Client.Lobby.UI;

/// <summary>Content-owned window; the browser supplies its title bar and close action.</summary>
public sealed class CharacterEditorWindow : BaseWindow
{
    public event Action? CloseRequested;
    public bool Maximized { get; private set; }
    private Vector2 _restoreSize, _restorePosition;
    private bool _restoreResizable;
    private Vector2? _pendingRestorePosition;

    public void ToggleMaximize()
    {
        if (Parent == null) return;
        if (!Maximized)
        {
            _pendingRestorePosition = null;
            _restoreSize = Size;
            _restorePosition = Position;
            _restoreResizable = Resizable;
            Maximized = true;
            Resizable = false;
            SetSize = Parent.Size;
            LayoutContainer.SetPosition(this, Vector2.Zero);
        }
        else
        {
            Maximized = false;
            Resizable = _restoreResizable;
            var size = Vector2.Min(_restoreSize, Parent.Size);
            SetSize = size;
            _pendingRestorePosition = _restorePosition;
            LayoutContainer.SetPosition(this, Vector2.Clamp(_restorePosition, Vector2.Zero, Vector2.Max(Vector2.Zero, Parent.Size - size)));
        }
    }

    public CharacterEditorWindow(Control editor)
    {
        MouseFilter = MouseFilterMode.Stop;
        MinSize = new Vector2(420, 360);
        editor.Margin = new Thickness(8);
        AddChild(editor);
    }

    public override void Close() => CloseRequested?.Invoke();
    protected override void Draw(DrawingHandleScreen handle) => CharacterWindowFrame.Draw(handle, PixelSize, UIScale, Resizable);
    public void CloseConfirmed() => base.Close();

    public void ShowEditor()
    {
        if (IsOpen) { MoveToFront(); return; }
        var available = UserInterfaceManager.WindowRoot.Size;
        MinSize = Vector2.Min(new Vector2(420, 360), available);
        SetSize = Vector2.Min(new Vector2(1220, 860), Vector2.Max(MinSize, available - new Vector2(32)));
        OpenCentered();
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (Parent == null) return;
        MinSize = Vector2.Min(new Vector2(420, 360), Parent.Size);
        var size = Maximized ? Parent.Size : Vector2.Min(_pendingRestorePosition != null ? _restoreSize : Size, Parent.Size);
        if (Size != size) SetSize = size;
        var position = Maximized ? Vector2.Zero : Vector2.Clamp(_pendingRestorePosition ?? Position, Vector2.Zero, Vector2.Max(Vector2.Zero, Parent.Size - size));
        if (_pendingRestorePosition != null && Size == size && Position == position) _pendingRestorePosition = null;
        LayoutContainer.SetPosition(this, position);
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

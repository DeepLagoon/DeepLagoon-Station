using System.Numerics;
using System.Linq;
using Content.Client.UserInterface.Controls;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>Common native window actions for every floating TGUI host.</summary>
public sealed class TguiWindowControls
{
    // ConditionalWeakTable is forbidden by the content sandbox. Windows own these
    // controls through their button callbacks; the registry must not keep them alive.
    private static readonly List<WeakReference<TguiWindowControls>> Windows = new();
    private static readonly List<RestoreButton> Minimized = new();
    private readonly BaseWindow _window;
    private readonly Button _maximize;
    private RestoreButton? _restore;
    private bool _maximized;
    private Vector2 _savedPosition, _savedSize, _savedMin, _savedMax;
    private bool _savedResizable;
    private string _title = "TGUI";

    public static TguiWindowControls Attach(BaseWindow window) => Attach(window, new Vector2(8, 8));

    public static TguiWindowControls Attach(BaseWindow window, Vector2 inset)
    {
        var existing = Find(window);
        if (existing != null) return existing;
        var controls = new TguiWindowControls(window, inset);
        Windows.Add(new WeakReference<TguiWindowControls>(controls));
        return controls;
    }

    public static bool IsMaximized(BaseWindow window) => Find(window)?._maximized ?? false;

    private static TguiWindowControls? Find(BaseWindow window)
    {
        TguiWindowControls? result = null;
        for (var i = Windows.Count - 1; i >= 0; i--)
        {
            if (!Windows[i].TryGetTarget(out var controls) || controls._window.Disposed)
            {
                Windows.RemoveAt(i);
                continue;
            }
            if (controls._window == window) result = controls;
        }
        return result;
    }
    public void SetTitle(string title) { _title = title; if (_restore != null) _restore.Text = title; }

    private TguiWindowControls(BaseWindow window, Vector2 inset)
    {
        _window = window;
        var buttonSize = new Vector2(26, 22);
        var minimize = new Button { Name = "MinimizeButton", Text = "—", ToolTip = "Свернуть окно", MinSize = buttonSize, MaxSize = buttonSize, VerticalAlignment = Control.VAlignment.Center };
        _maximize = new Button { Text = "□", ToolTip = "Развернуть на весь экран", MinSize = buttonSize, MaxSize = buttonSize, VerticalAlignment = Control.VAlignment.Center };
        minimize.AddStyleClass("ButtonSquare");
        _maximize.AddStyleClass("ButtonSquare");
        minimize.OnPressed += _ => Minimize();
        _maximize.OnPressed += _ => ToggleMaximize();
        if (window is FancyWindow)
        {
            var close = window.FindControl<TextureButton>("CloseButton");
            close.MinSize = close.MaxSize = buttonSize;
            // Long entity names must yield space to the window actions.
            window.FindControl<Label>("WindowTitle").ClipText = true;
            var parent = close.Parent!;
            var index = close.GetPositionInParent();
            parent.AddChild(minimize); minimize.SetPositionInParent(index);
            parent.AddChild(_maximize); _maximize.SetPositionInParent(index + 1);
        }
        else
        {
            var row = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, MouseFilter = Control.MouseFilterMode.Stop,
                HorizontalAlignment = Control.HAlignment.Right, VerticalAlignment = Control.VAlignment.Top, Margin = new Thickness(0, inset.Y, inset.X, 0) };
            row.AddChild(minimize); row.AddChild(_maximize);
            var close = new Button { Text = "×", ToolTip = "Закрыть окно", MinSize = buttonSize, MaxSize = buttonSize, VerticalAlignment = Control.VAlignment.Center };
            close.OnPressed += _ => window.Close();
            row.AddChild(close);
            window.AddChild(row);
        }
        window.OnClose += RemoveRestoreButton;
    }

    private void Minimize()
    {
        if (_window.Parent == null || _restore != null) return;
        _restore = new RestoreButton(this) { Text = _title, ToolTip = "Вернуть окно: " + _title, MinSize = new(160, 30), MaxWidth = 172, RectClipContent = true };
        _window.Parent.AddChild(_restore);
        // BaseWindow.IsAtFront also considers non-window siblings. Keep the dock
        // behind floating windows so it cannot disable another window's keyboard focus.
        _restore.SetPositionFirst();
        Minimized.Add(_restore);
        _window.Visible = false;
        _window.UserInterfaceManager.KeyboardFocused?.ReleaseKeyboardFocus();
        PositionRestoreButton();
    }

    private void Restore()
    {
        if (_window.Disposed || _window.Parent == null) { RemoveRestoreButton(); return; }
        RemoveRestoreButton();
        _window.Visible = true;
        _window.MoveToFront();
        Update();
        FocusDocument();
    }

    private void RemoveRestoreButton()
    {
        if (_restore == null) return;
        var button = _restore; _restore = null;
        Minimized.Remove(button);
        button.Orphan();
#pragma warning disable CS0618
        button.Dispose();
#pragma warning restore CS0618
    }

    private void PositionRestoreButton()
    {
        if (_restore?.Parent is not { } root) return;
        var index = Minimized.Where(button => button.Parent == root).ToList().IndexOf(_restore);
        var columns = Math.Max(1, (int) root.Size.X / 180);
        LayoutContainer.SetPosition(_restore, new(8 + index % columns * 180, Math.Max(0, root.Size.Y - 38 - index / columns * 34)));
        _restore.SetSize = new(172, 30);
    }

    private void ToggleMaximize()
    {
        if (_window.Parent == null) return;
        _window.MoveToFront();
        if (!_maximized)
        {
            _savedPosition = _window.Position; _savedSize = _window.Size;
            _savedMin = _window.MinSize; _savedMax = _window.MaxSize; _savedResizable = _window.Resizable;
            _maximized = true;
            _maximize.Text = "▣"; _maximize.ToolTip = "Вернуть прежний размер";
            _window.MaxSize = new(float.PositiveInfinity);
            _window.Resizable = false;
            Update();
        }
        else
        {
            _maximized = false;
            _maximize.Text = "□"; _maximize.ToolTip = "Развернуть на весь экран";
            _window.MinSize = _savedMin; _window.MaxSize = _savedMax; _window.Resizable = _savedResizable;
            _window.SetSize = _savedSize;
            LayoutContainer.SetPosition(_window, _savedPosition);
        }
        FocusDocument();
    }

    private void FocusDocument()
    {
        _window.UserInterfaceManager.DeferAction(() =>
        {
            if (_window.Disposed || !_window.VisibleInTree || !_window.IsOpen || !_window.IsAtFront()) return;
            bool Visit(Control control)
            {
                if (control is GameWebView web) { web.FocusInput(); web.SyncInputFocus(); return true; }
                foreach (var child in control.Children) if (child.VisibleInTree && Visit(child)) return true;
                return false;
            }
            Visit(_window);
        });
    }

    public void Update()
    {
        if (_maximized && _window.Parent is { } parent)
        {
            _window.MinSize = Vector2.Min(_savedMin, parent.Size);
            if (_window.SetSize != parent.Size) _window.SetSize = parent.Size;
            LayoutContainer.SetPosition(_window, Vector2.Zero);
        }
    }

    private sealed class RestoreButton : Button
    {
        private readonly TguiWindowControls _controls;
        public RestoreButton(TguiWindowControls controls) { _controls = controls; OnPressed += _ => controls.Restore(); }
        protected override void FrameUpdate(FrameEventArgs args)
        {
            base.FrameUpdate(args);
            if (_controls._window.Disposed || _controls._window.Parent != Parent) { UserInterfaceManager.DeferAction(_controls.RemoveRestoreButton); return; }
            _controls.PositionRestoreButton();
        }
        [Obsolete("Controls should only be removed from UI tree instead of being disposed")]
        protected override void Dispose(bool disposing)
        {
            Minimized.Remove(this);
            if (_controls._restore == this) _controls._restore = null;
            base.Dispose(disposing);
        }
    }
}

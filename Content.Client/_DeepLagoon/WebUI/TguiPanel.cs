using Content.Shared._DeepLagoon.WebUI;
using Robust.Client.UserInterface;
using System.Numerics;
using Robust.Client.Input;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Map;
using Robust.Shared.Timing;
using Robust.Shared.Configuration;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>Reusable embedded TGUI: state replay, transport and lifecycle for every content window.</summary>
public sealed partial class TguiPanel : Control
{
    public static bool Enabled => WebUiCVars.UseTgui(IoCManager.Resolve<IConfigurationManager>().GetCVar(WebUiCVars.InterfaceStyle));
    private Action? _nativeFallback;
    private Action? _tguiRestore;
    private bool _webSwitchBlocked;
    private Button? _styleButton;
    private bool _styleBound;
    private bool _styleInitialized;
    /// <summary>Separate window types may share a frontend component but keep independent choices.</summary>
    public string? InterfaceStyleKey { get; set; }
    private string StyleKey => InterfaceStyleKey ?? (_interface == "ChatComposer" ? "Chat" : _interface);
    public void SetTguiRestore(Action restore) => _tguiRestore = restore;
    public void BindStyle(Control owner, string key, Func<bool> current, Action<bool> change, Func<bool, bool>? canChange = null)
    {
        _styleBound = true;
        _styleButton = WindowInterfaceStyle.Attach(owner, key, current, next =>
        {
            if ((!next && _webSwitchBlocked) || canChange?.Invoke(next) == false) return;
            WindowInterfaceStyle.Save(key, next);
            change(next);
        });
    }

    public bool SwitchToTgui()
    {
        if (Disposed || !_usingNative) return !Disposed;
        if (!CanCloseNative()) return false;
        _usingNative = false;
        if (_styleButton != null) _styleButton.Text = "SS14";
        if (_native != null) _native.Visible = false;
        _nativeUpdates.Clear();
        Web.Visible = true;
        _tguiRestore?.Invoke();
        NativeModeChanged?.Invoke();
        Publish();
        return true;
    }

    private void SelectStyle(bool tgui)
    {
        if (!tgui && _webSwitchBlocked) return;
        if (tgui && !SwitchToTgui()) return;
        WindowInterfaceStyle.Save(StyleKey, tgui);
        if (!tgui) SwitchToNative();
    }
    private NativeInterfacePanel? _native;
    private bool _usingNative;
    private bool _modeChangePending;
    private readonly Queue<string> _nativeUpdates = new();
    public bool UsingNative => _usingNative;
    public event Action? NativeModeChanged;
    public bool CanCloseNative() => _native?.CanClose() ?? true;

    /// <summary>Existing windows keep their typed native controls and state owners.</summary>
    public void SetNativeFallback(Action fallback) => _nativeFallback = fallback;

    public void SwitchToNative()
    {
        if (Disposed || _usingNative) return;
        EndWindowDrag();
        ClearConsoleKeys();
        _usingNative = true;
        _webSwitchBlocked = false;
        if (_styleButton != null) { _styleButton.Text = "TGUI"; _styleButton.Disabled = false; }
        _failure.Visible = false;
        Web.Visible = false;
        Web.PauseBrowser();
        NativeModeChanged?.Invoke();
        if (_nativeFallback != null) { _nativeFallback(); return; }
        if (_native != null) { _native.Visible = true; _native.UpdateState(_interface, _data); return; }
        _native = new NativeInterfacePanel((action, payload) => OnAction?.Invoke(action, payload), () => OnClose?.Invoke());
        AddChild(_native);
        _native.UpdateState(_interface, _data);
        foreach (var update in _nativeUpdates) _native.Receive("update", update);
        _nativeUpdates.Clear();
    }
    public GameWebView Web { get; }
    public event Action<string, string>? OnAction;
    public event Action? OnClose;
    private string _interface = "";
    private string _data = "{}";
    private string _title = "DeepLagoon";
    private string? _staticData;
    private TguiData? _incrementalData;
    private IInputManager? _input;
    private BaseWindow? _dragWindow;
    private ScreenCoordinates _dragPointer;
    private Vector2 _dragOrigin;
    private bool _dragging;
    private string _chatState = "";
    private string? _chatSettings;
    private float _appearanceRefresh;
    private readonly bool _inheritChatAppearance;
    private readonly bool _showWindowControls;
    private TguiWindowControls? _windowControls;
    private readonly BoxContainer _failure = new() { Orientation = BoxContainer.LayoutOrientation.Vertical, Visible = false };

    public TguiPanel(bool suspendWhenHidden = false, bool inheritChatAppearance = true, bool captureKeyboard = false, bool showWindowControls = true)
    {
        _inheritChatAppearance = inheritChatAppearance;
        _showWindowControls = showWindowControls;
        // All panels suspend while hidden, including native-only sessions. No hidden CEF windows.
        Web = new GameWebView(suspendWhenHidden: true, captureKeyboard: captureKeyboard) { Visible = false };
        Web.Sending += (type, payload) =>
        {
            if (_usingNative && _native != null) { _native.Receive(type, payload); return; }
            if (type != "update") return;
            if (_nativeUpdates.Count >= 8) _nativeUpdates.Dequeue();
            _nativeUpdates.Enqueue(payload);
        };
        HorizontalExpand = VerticalExpand = true;
        AddChild(Web);
        _failure.AddChild(new Label { Text = "Не удалось запустить TGUI. Повторите загрузку интерфейса." });
        var retry = new Button { Text = "Повторить" };
        retry.OnPressed += _ => { _failure.Visible = false; Web.Reload(); };
        _failure.AddChild(retry);
        AddChild(_failure);
        Web.Failed += SwitchToNative;
        Web.Ready += () => _failure.Visible = false;
        Web.Ready += Publish;
        // A hidden document may outlive the bounded outgoing queue. Replay the
        // latest snapshot on resume so dropped deltas cannot leave stale fields.
        Web.Resumed += Publish;
        Web.Message += (type, payload) =>
        {
            if (type == "act/__style-blocked")
            {
                if (TguiActionData.TryParse(payload, out var args) && args != null)
                {
                    _webSwitchBlocked = args.String("blocked") == "true";
                    if (_styleButton != null)
                    {
                        _styleButton.Disabled = _webSwitchBlocked;
                        _styleButton.ToolTip = Loc.GetString(_webSwitchBlocked ? "dl-ui-window-unsaved" : "dl-ui-window-switch");
                    }
                }
                return;
            }
            if (type == "close") { OnClose?.Invoke(); return; }
            if (type.StartsWith("act/", StringComparison.Ordinal) && type.Length is > 4 and <= 68 &&
                payload.Length <= WebUiActionMessage.MaxPayloadLength)
                OnAction?.Invoke(type[4..], payload);
        };
    }

    public void SetState(string interfaceName, string dataJson, string title = "DeepLagoon")
    {
        if (_interface == interfaceName && _data == dataJson && _title == title) return;
        _interface = interfaceName;
        _data = dataJson;
        _title = title;
        if (!_styleInitialized) Web.Visible = WindowInterfaceStyle.UseTgui(StyleKey);
        if (_usingNative) _native?.UpdateState(_interface, _data);
        Publish();
    }

    public void SetStaticData(string dataJson)
    {
        if (_staticData == dataJson) return;
        _staticData = dataJson;
        _incrementalData = null;
    }

    /// <summary>Keep a full replay snapshot, but send only changed dynamic fields while the document is alive.</summary>
    public void SetIncrementalState(string interfaceName, TguiData data, string title = "DeepLagoon")
    {
        if (!_styleInitialized) Web.Visible = WindowInterfaceStyle.UseTgui(InterfaceStyleKey ?? (interfaceName == "ChatComposer" ? "Chat" : interfaceName));
        var previous = _incrementalData;
        _incrementalData = data;
        _data = data.ToString();
        if (_usingNative) _native?.UpdateState(interfaceName, _data);
        if (previous == null || _interface != interfaceName || _title != title)
        {
            _interface = interfaceName;
            _title = title;
            Publish();
            return;
        }
        if (!Web.IsReady) return;
        var changes = data.ChangesFrom(previous).ToString();
        var removed = data.RemovedSince(previous);
        if (changes == "{}" && removed == "[]") return;
        Web.Send("update", "{\"data\":" + changes + ",\"removedData\":" + removed + "}");
    }

    protected override void EnteredTree()
    {
        base.EnteredTree();
        _input = IoCManager.Resolve<IInputManager>();
        _input.FirstChanceOnKeyEvent += OnWindowPointer;
        UserInterfaceManager.DeferAction(() =>
        {
            if (!_showWindowControls || Disposed || !IsInsideTree || FloatingWindow() is not { } window) return;
            _windowControls = TguiWindowControls.Attach(window);
            _windowControls.SetTitle(_title);
        });
    }

    protected override void ExitedTree()
    {
        ClearConsoleKeys();
        EndWindowDrag();
        if (_input != null) _input.FirstChanceOnKeyEvent -= OnWindowPointer;
        _input = null;
        base.ExitedTree();
    }

    private BaseWindow? FloatingWindow()
    {
        for (Control? ancestor = Parent; ancestor != null; ancestor = ancestor.Parent)
            if (ancestor is BaseWindow window && window.Parent == UserInterfaceManager.WindowRoot)
                return window;
        return null;
    }

    private void OnWindowPointer(KeyEventArgs args, KeyEventType type)
    {
        OnConsoleKey(args, type);
        if (args.Key != Keyboard.Key.MouseLeft || _input == null) return;
        if (type == KeyEventType.Up)
        {
            UpdateWindowDrag(_input.MouseScreenPosition);
            EndWindowDrag();
            return;
        }
        if (args.Handled || args.IsRepeat || type != KeyEventType.Down || !VisibleInTree || !Web.IsReady ||
            FloatingWindow() is not { } window || TguiWindowControls.IsMaximized(window)) return;
        var pointer = _input.MouseScreenPosition;
        // Color wheels, sliders, selections and scrollbars own drags in the body.
        if (pointer.Position.Y / UIScale - GlobalPosition.Y > 52) return;
        for (var hit = UserInterfaceManager.MouseGetControl(pointer); hit != null; hit = hit.Parent)
        {
            if (hit != this) continue;
            BeginWindowDrag(window, pointer);
            break;
        }
    }

    private void BeginWindowDrag(BaseWindow window, ScreenCoordinates pointer)
    {
        _dragWindow = window;
        _dragPointer = pointer;
        _dragOrigin = window.Position;
        _dragging = false;
    }

    private void UpdateWindowDrag(ScreenCoordinates pointer)
    {
        if (_dragWindow is not { Parent: not null } window || pointer.Window != _dragPointer.Window) return;
        var delta = (pointer.Position - _dragPointer.Position) / UIScale;
        if (!_dragging)
        {
            // A click stays a click. Only actual movement turns it into a drag.
            if (delta.LengthSquared() < 16) return;
            _dragging = true;
            Web.SetWindowDragging(true);
            UserInterfaceManager.DeferAction(() =>
            {
                if (!window.Disposed && window.Parent != null) window.MoveToFront();
            });
        }
        LayoutContainer.SetPosition(window, Vector2.Clamp(_dragOrigin + delta,
            Vector2.Zero, Vector2.Max(Vector2.Zero, window.Parent!.Size - window.Size)));
    }

    private void EndWindowDrag()
    {
        var window = _dragWindow;
        if (_dragging)
        {
            Web.SetWindowDragging(false);
            // Mouse-up bindings and CEF can change focus after the first-chance
            // handler. Restore both native keyboard/text input and DOM focus
            // after that processing, without stealing it from another window.
            UserInterfaceManager.DeferAction(() =>
            {
                if (!Disposed && VisibleInTree && window is { Disposed: false, Parent: not null } &&
                    FloatingWindow() == window && window.IsAtFront())
                {
                    for (var captured = UserInterfaceManager.ControlFocused; captured != null; captured = captured.Parent)
                    {
                        if (captured != this) continue;
                        UserInterfaceManager.ControlFocused = null;
                        break;
                    }
                    Web.SyncInputFocus();
                }
            });
        }
        _dragging = false;
        _dragWindow = null;
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (!ConsoleKeyboardFeedback || !VisibleInTree || !Web.IsReady || UsingNative || FloatingWindow() is not { } console || !console.IsAtFront())
            ClearConsoleKeys();
        if (!_styleInitialized && _interface.Length > 0 && !_modeChangePending)
        {
            _styleInitialized = true;
            // Bind outside traversal: typed fallbacks can replace and dispose their host.
            _modeChangePending = true;
            Web.Visible = WindowInterfaceStyle.UseTgui(StyleKey);
            UserInterfaceManager.DeferAction(() =>
            {
                _modeChangePending = false;
                if (Disposed) return;
                if (!_styleBound)
                {
                    _styleBound = true;
                    _styleButton = WindowInterfaceStyle.Attach(FloatingWindow() ?? (Control) this, StyleKey, () => !_usingNative, SelectStyle);
                    if (_showWindowControls && FloatingWindow() is { } host &&
                        host is not Content.Client.UserInterface.Controls.FancyWindow && host is not DefaultWindow)
                        _styleButton.Margin = new Thickness(0, 8, 96, 0);
                }
                if (!WindowInterfaceStyle.UseTgui(StyleKey)) SwitchToNative();
            });
        }
        _windowControls?.SetTitle(_title);
        _windowControls?.Update();
        _appearanceRefresh -= args.DeltaSeconds;
        if (_inheritChatAppearance && VisibleInTree && _appearanceRefresh <= 0)
        {
            _appearanceRefresh = 0.2f;
            var preferences = IoCManager.Resolve<Content.Client.Lobby.IClientPreferencesManager>();
            var settings = preferences.Preferences?.ChatPanelSettings ?? "";
            // Saved chat settings can be large. Parse only when the source changes,
            // including caching malformed settings instead of retrying every frame.
            if (!string.Equals(settings, _chatSettings, StringComparison.Ordinal))
            {
                _chatSettings = settings;
                var state = Content.Client.UserInterface.Systems.Chat.Controls.ChatTabsSettings.Deserialize(settings)?.WebState ?? "";
                if (state != _chatState) { _chatState = state; Publish(); }
            }
        }
        if (_dragWindow == null || _input == null) return;
        if (!VisibleInTree || FloatingWindow() != _dragWindow || !_input.IsKeyDown(Keyboard.Key.MouseLeft))
        {
            EndWindowDrag();
            return;
        }
        UpdateWindowDrag(_input.MouseScreenPosition);
    }

    protected override void VisibilityChanged(bool newVisible)
    {
        base.VisibilityChanged(newVisible);
        if (!VisibleInTree) { EndWindowDrag(); ClearConsoleKeys(); }
    }

    private void Publish()
    {
        if (_interface.Length == 0 || !Web.IsReady) return;
        var data = _data.Trim();
        if (data.Length < 2 || data[0] != '{' || data[^1] != '}') data = "{}";
        Web.Send("update", "{\"replaceData\":true," + (_staticData == null ? "" : "\"replaceStaticData\":true,\"resetPartialData\":true,\"static_data\":" + _staticData + ",") +
            "\"config\":{\"interface\":" + GameWebView.Quote(_interface) +
            ",\"title\":" + GameWebView.Quote(_title) +
            ",\"status\":2,\"window\":{\"key\":\"deeplagoon\",\"fancy\":false}},\"data\":" + data[..^1] +
            (string.IsNullOrWhiteSpace(data[1..^1]) ? "" : ",") + "\"chatState\":" + GameWebView.Quote(_chatState) + "}}");
    }
}

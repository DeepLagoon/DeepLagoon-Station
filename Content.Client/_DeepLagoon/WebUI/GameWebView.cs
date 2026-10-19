using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Shared.Configuration;
using Robust.Client.UserInterface;
using Robust.Client.WebView;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>
/// Local packaged web UI. CEF callbacks run on another thread: only enqueue here;
/// dispatch messages after the UI frame traversal. Remote documents and downloads are rejected.
/// </summary>
public sealed class GameWebView : Control
{
    private const string Root = "/Web/DeepLagoon/";
    private static readonly byte[] BridgeAck = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
    private WebViewControl? _view;
    private readonly string _documentUrl;
    private readonly string? _devOrigin;
    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly Queue<string> _outgoing = new();
    private readonly StringBuilder _chunkPayload = new();
    private string _chunkType = "";
    private int _chunkIndex;
    private int _chunkCount;
    private bool _ready;
    private bool _disposed;
    private bool _shuttingDown;
    private bool _messageDispatchPending;
    private bool _explicitTextInput;
    private float _healthWait;
    private float _pingWait;
    private bool _healthFailed;
    private bool _recovering;
    private bool _warmSuspended;
    private bool _warmTimerPending;
    private TimeSpan _warmUntil;
    private readonly System.Threading.CancellationTokenSource _warmCancellation = new();
    private bool CanKeepWarm => _suspendWhenHidden && WebViewCapabilities.NativeLifecycle;
    private readonly bool _suspendWhenHidden;
    private readonly bool _captureKeyboard;
    private readonly NativeChatBackground _background;
    public bool BrowserActive => _view?.IsInsideTree == true;
    public bool IsReady => _ready;
    public event Action<string, string>? Message;
    public event Action? Ready;
    public event Action? Failed;
    public event Action? Resumed;
    internal event Action<string, string>? Sending;

    public void FocusInput() => _view?.GrabKeyboardFocus();
    public void SyncInputFocus()
    {
        if (_ready && !_disposed) _view?.ExecuteJavaScript("window.DeepLagoonHealth?.syncFocus();");
    }

    public bool HasInputFocus => _view?.HasKeyboardFocus() == true;

    public void SendGridEditorKey(string code, bool down, bool control, bool shift, bool alt)
    {
        if (!_ready || _disposed) return;
        var detail = new TguiData().String("code", code).Bool("down", down).Bool("ctrlKey", control)
            .Bool("shiftKey", shift).Bool("altKey", alt).ToString();
        _view?.ExecuteJavaScript("window.dispatchEvent(new CustomEvent('grid-editor-key',{detail:" + detail + "}));");
    }

    public void SetWindowDragging(bool dragging)
    {
        if (!_ready || _disposed) return;
        _view?.ExecuteJavaScript("window.dispatchEvent(new CustomEvent('deeplagoon/window-drag', {detail:" +
            (dragging ? "true" : "false") + "}));");
    }

    public void FocusTextInput()
    {
        FocusInput();
        _explicitTextInput = true;
        _view?.Root?.Window?.TextInputStart();
        if (_ready)
            _view?.ExecuteJavaScript("window.focus(); window.dispatchEvent(new Event('deeplagoon/focus-input'));");
    }

    // Compatibility path for the old official module's incorrect Return char event.
    public void SendInputKey(bool tab, bool shift)
    {
        if (!_ready || _disposed || !HasInputFocus) return;
        _view?.ExecuteJavaScript("document.activeElement?.dispatchEvent(new KeyboardEvent('keydown', {" +
            (tab ? "key:'Tab',code:'Tab',keyCode:9,which:9," : "key:'Enter',code:'Enter',keyCode:13,which:13,") +
            "bubbles:true,cancelable:true,shiftKey:" +
            (shift ? "true" : "false") + "}));");
    }

    public void ReleaseTextInput()
    {
        if (_explicitTextInput && HasInputFocus) _view?.Root?.Window?.TextInputStop();
        _explicitTextInput = false;
    }

    public void PrepareForShutdown()
    {
        if (_disposed || _shuttingDown) return;
        _shuttingDown = true;
        _warmCancellation.Cancel();
        // EntryPoint calls this while CEF is still alive. Later entity/UI
        // teardown (including window dragging) must not execute JavaScript.
        ReleaseTextInput();
        _ready = false;
        _outgoing.Clear();
        while (_incoming.TryDequeue(out _)) { }
    }

    /// <summary>Release CEF for a native view, while allowing this host to start again later.</summary>
    public void PauseBrowser()
    {
        if (_disposed || _shuttingDown) return;
        SuspendBrowser(recreate: false);
        _view = null;
        _ready = false;
        _healthFailed = _recovering = _warmSuspended = false;
        _background.Reset();
        _outgoing.Clear();
        _chunkPayload.Clear();
        while (_incoming.TryDequeue(out _)) { }
    }

    public void StopBrowser()
    {
        PrepareForShutdown();
        SuspendBrowser();
    }

    public GameWebView(bool chat = false, bool suspendWhenHidden = false, bool captureKeyboard = false)
    {
        _captureKeyboard = captureKeyboard;
        _suspendWhenHidden = suspendWhenHidden;
        HorizontalExpand = VerticalExpand = true;
        // Interfaces such as the character editor use the same native layer.
        // It draws nothing until the document publishes an enabled preset.
        _background = new NativeChatBackground();
        AddChild(_background);
#if DEBUG
        _devOrigin = TguiDevelopmentPolicy.GetOrigin(IoCManager.Resolve<IConfigurationManager>().GetCVar(WebUiCVars.DevServer));
#endif
        _documentUrl = (_devOrigin == null ? "res://deeplagoon" + Root : _devOrigin + "/") +
            (chat ? "chat.html" : "interface.html");
        // Defer module/browser creation until a visible frame. Classic mode needs no CEF at all.
    }

    private WebViewControl CreateView()
    {
        var view = new WebViewControl
        {
            HorizontalExpand = true,
            VerticalExpand = true,
            // Stock WebView delivers mouse clicks through keyboard-focused raw
            // input. The chat bridge releases focus after non-editable clicks.
            KeyboardFocusOnClick = true,
            Url = _documentUrl
        };
        view.AddBeforeBrowseHandler(BeforeBrowse);
        view.AddResourceRequestHandler(ResourceRequest);
        WebViewLifetime.Register(view, PrepareForShutdown);
        return view;
    }

    private static bool IsLocal(Uri uri) => uri.Scheme == "res" &&
        uri.Host == "deeplagoon" && uri.AbsolutePath.StartsWith(Root, StringComparison.Ordinal);

    private void BeforeBrowse(IBeforeBrowseContext context)
    {
        if (!WikiNavigationPolicy.TryUri(context.Url, out var uri) ||
            (!IsLocal(uri) && !TguiDevelopmentPolicy.IsDocument(uri, _devOrigin)))
            context.DoCancel();
    }

    private void ResourceRequest(IRequestHandlerContext context)
    {
        // Bundles embed fonts and images as data URIs; these have no network access.
        if (!context.IsNavigation && !context.IsDownload && context.Url.StartsWith("data:", StringComparison.Ordinal))
            return;
        if (!WikiNavigationPolicy.TryUri(context.Url, out var uri) || context.IsDownload ||
            (!IsLocal(uri) && !TguiDevelopmentPolicy.IsResource(uri, _devOrigin)))
        {
            context.DoCancel();
            return;
        }
        if (uri.AbsolutePath != Root + "bridge")
            return;
        if (context.Method != "GET" || uri.Query.Length > 65536 ||
            (context.RequestInitiator != "res://deeplagoon" && context.RequestInitiator != _devOrigin))
        {
            context.DoCancel();
            return;
        }
        // Query contains a percent-encoded message type and JSON payload.
        if (_incoming.Count >= 64 || _disposed)
        {
            context.DoCancel();
            return;
        }
        // Official 277 exposes no POST body API. The bounded GET/chunk bridge
        // works with its signed module and keeps content independent of forks.
        _incoming.Enqueue(uri.Query.TrimStart('?'));
        context.DoRespondStream(new MemoryStream(BridgeAck, writable: false), "image/gif");
    }

    public void Send(string type, string payloadJson)
    {
        if (!_disposed) Sending?.Invoke(type, payloadJson);
        if (_disposed || _shuttingDown) return;
        if (CanKeepWarm && !VisibleInTree && type == "chat/message") return;
        var json = "{\"type\":" + Quote(type) + ",\"payload\":" + payloadJson + "}";
        if (!_ready || CanKeepWarm && !VisibleInTree)
        {
            if (_outgoing.Count < 256)
                _outgoing.Enqueue(json);
            return;
        }
        Dispatch(json);
    }

    public void Reload()
    {
        if (_disposed || _shuttingDown || !BrowserActive) return;
        _ready = false;
        _healthWait = _pingWait = 0;
        _healthFailed = false;
        _recovering = false;
        _background?.Reset();
        _outgoing.Clear();
        while (_incoming.TryDequeue(out _)) { }
        _chunkPayload.Clear();
        _view?.Reload();
    }

    private void Dispatch(string json) => _view?.ExecuteJavaScript("try { window.update(" +
        Quote(json) + "); } catch (error) { console.error(error); }");

    /// <summary>JSON string encoding without adding non-sandboxed client dependencies.</summary>
    public static string Quote(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            if (c == '\\' || c == '"') builder.Append('\\').Append(c);
            else if (c < ' ' || c == '\u2028' || c == '\u2029')
                builder.Append("\\u").Append(((int)c).ToString("x4"));
            else builder.Append(c);
        }
        return builder.Append('"').ToString();
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (_disposed || _shuttingDown) return;
        // Attach only once the visible viewport is laid out. Hidden editors do
        // not create background CEF browsers, even while their UI stays in tree.
        if (VisibleInTree && _view?.Parent == null && !_healthFailed)
        {
            try
            {
                _view ??= CreateView();
                AddChild(_view);
                if (CanKeepWarm) _view.AlwaysActive = true;
            }
            catch (Exception e)
            {
                _healthFailed = true;
                Robust.Shared.Log.Logger.GetSawmill("tgui").Error($"TGUI browser could not start: {e.GetType().Name}");
                UserInterfaceManager.DeferAction(() => { if (!_disposed && !_shuttingDown) Failed?.Invoke(); });
                return;
            }
        }
        if (_warmSuspended && VisibleInTree)
        {
            _warmSuspended = false;
            _healthWait = _pingWait = 0;
            if (_ready)
            {
                while (_outgoing.TryDequeue(out var queued)) Dispatch(queued);
                UserInterfaceManager.DeferAction(() =>
                {
                    if (!_disposed && VisibleInTree && _ready) Resumed?.Invoke();
                });
            }
        }
        if (!_disposed && BrowserActive && VisibleInTree && !_healthFailed)
        {
            _healthWait += args.DeltaSeconds;
            _pingWait += args.DeltaSeconds;
            if (_ready && _pingWait >= 2)
            {
                _pingWait = 0;
                _view?.ExecuteJavaScript("window.DeepLagoonHealth?.ping();");
            }
            if (_healthWait >= 15)
            {
                UserInterfaceManager.DeferAction(() =>
                {
                    if (_disposed || !VisibleInTree || _healthWait < 15)
                    {
                        _healthFailed = false;
                        return;
                    }
                    if (!_recovering)
                    {
                        Robust.Shared.Log.Logger.GetSawmill("tgui").Warning("TGUI health check timed out; reloading the page.");
                        Reload();
                        _recovering = true;
                        return;
                    }
                    _healthFailed = true;
                    _ready = false;
                    ReleaseTextInput();
                    _view?.ReleaseKeyboardFocus();
                    Robust.Shared.Log.Logger.GetSawmill("tgui").Error("TGUI recovery failed; using the native fallback or retry control.");
                    Failed?.Invoke();
                });
                _healthFailed = true;
            }
        }
        if (_disposed || _messageDispatchPending || _incoming.IsEmpty) return;
        _messageDispatchPending = true;
        // Actions can add or remove windows (including this browser's owner).
        // Never invoke them while an ancestor enumerates its child controls.
        UserInterfaceManager.DeferAction(() =>
        {
            _messageDispatchPending = false;
            DispatchIncoming();
        });
    }

    private void DispatchIncoming()
    {
        for (var i = 0; i < 32 && _incoming.TryDequeue(out var json); i++)
        {
            if (_disposed)
                return;
            var fields = json.Split('&', 2);
            if (fields.Length != 2) continue;
            var type = Uri.UnescapeDataString(fields[0]);
            var payload = Uri.UnescapeDataString(fields[1]);
            if (type == "bridge/chunk")
            {
                if (!TguiActionData.TryParse(payload, out var chunk) ||
                    !chunk!.TryInt("index", out var index) || !chunk.TryInt("count", out var count) ||
                    count is < 1 or > 17 || chunk.String("text") is not { Length: <= 2000 } part ||
                    chunk.String("messageType") is not { } originalType ||
                    !(originalType == "panel/state_set" || originalType.StartsWith("act/", StringComparison.Ordinal))) continue;
                if (index == 0)
                {
                    _chunkPayload.Clear();
                    _chunkType = originalType;
                    _chunkCount = count;
                    _chunkIndex = 0;
                }
                if (originalType != _chunkType || count != _chunkCount || index != _chunkIndex ||
                    _chunkPayload.Length + part.Length > 32768) { _chunkPayload.Clear(); _chunkIndex = -1; continue; }
                _chunkPayload.Append(part);
                _chunkIndex++;
                if (_chunkIndex != count) continue;
                type = _chunkType;
                payload = _chunkPayload.ToString();
                _chunkPayload.Clear();
                _chunkIndex = -1;
            }
            if (type.Length > 68 || payload.Length > (type == "panel/state_set" ? 32768 : WebUiActionMessage.MaxPayloadLength)) continue;
            switch (type)
            {
                case "ready":
                    // Vite may reload the document after an HTML/non-component edit.
                    // Keep the native window open and let its owner resend current state.
                    if (!WebViewCapabilities.NativeLifecycle)
                    {
                        _view?.Arrange(UIBox2.FromDimensions(System.Numerics.Vector2.Zero, Size + System.Numerics.Vector2.One));
                        InvalidateArrange();
                    }
                    _ready = true;
                    _healthWait = 0;
                    if (WebViewCapabilities.NativeLifecycle && _devOrigin == null)
                        _view?.ExecuteJavaScript("window.__deeplagoonFetchBridge = true;");
                    if (_captureKeyboard) _view?.ExecuteJavaScript("window.__deeplagoonCaptureKeys = true;");
                    if (_background != null)
                        _view?.ExecuteJavaScript("window.__deeplagoonNativeBackground = true; window.dispatchEvent(new Event('deeplagoon/native-background'));");
                    while (_outgoing.TryDequeue(out var queued))
                        Dispatch(queued);
                    Ready?.Invoke();
                    break;
                case "health/pong":
                    _healthWait = 0;
                    _recovering = false;
                    break;
                case "health/error":
                    Robust.Shared.Log.Logger.GetSawmill("tgui").Error("Unhandled script error in packaged TGUI; scheduling recovery.");
                    _healthWait = 15;
                    break;
                case "native-background":
                    _background?.Configure(payload);
                    break;
                case "chat-input-focus":
                    if (!VisibleInTree || _view?.IsInsideTree != true ||
                        !TguiActionData.TryParse(payload, out var focus)) break;
                    if (focus!.String("active") == "true")
                    {
                        FocusInput();
                        if (focus.String("text") == "false") ReleaseTextInput();
                        else
                        {
                            _explicitTextInput = true;
                            _view?.Root?.Window?.TextInputStart();
                        }
                    }
                    else if (focus.String("active") == "false")
                    {
                        ReleaseTextInput();
                        // Do not release another window's focus (e.g. the composer).
                        _view?.ReleaseKeyboardFocus();
                    }
                    break;
                default:
                    Message?.Invoke(type, payload);
                    break;
            }
        }
    }

    protected override void ExitedTree()
    {
        base.ExitedTree();
        if (CanKeepWarm && !_disposed && _view?.AlwaysActive == true)
        {
            KeepWarm();
            return;
        }
        if (_suspendWhenHidden && !_disposed) SuspendBrowser();
        _ready = false;
        _background?.Reset();
        _outgoing.Clear();
        while (_incoming.TryDequeue(out _)) { }
    }

    protected override void VisibilityChanged(bool newVisible)
    {
        base.VisibilityChanged(newVisible);
        if (!_suspendWhenHidden || VisibleInTree || _view?.Parent == null) return;
        if (CanKeepWarm && _view.AlwaysActive)
        {
            KeepWarm();
            return;
        }
        SuspendBrowser();
        _ready = false;
        _background?.Reset();
        _outgoing.Clear();
        while (_incoming.TryDequeue(out _)) { }
    }

    private void KeepWarm()
    {
        ReleaseTextInput();
        _view?.ReleaseKeyboardFocus();
        _warmSuspended = true;
        _warmUntil = IoCManager.Resolve<IGameTiming>().RealTime + TimeSpan.FromSeconds(30);
        if (_warmTimerPending) return;
        ScheduleWarmClose();
    }

    private void ScheduleWarmClose()
    {
        _warmTimerPending = true;
        var delay = Math.Max(1, (int)Math.Ceiling((_warmUntil - IoCManager.Resolve<IGameTiming>().RealTime).TotalMilliseconds));
        Timer.Spawn(delay, () => UserInterfaceManager.DeferAction(CheckWarmExpiry), _warmCancellation.Token);
    }

    private void CheckWarmExpiry()
    {
        _warmTimerPending = false;
        if (_disposed || VisibleInTree || !_warmSuspended) return;
        if (IoCManager.Resolve<IGameTiming>().RealTime < _warmUntil) { ScheduleWarmClose(); return; }
        SuspendBrowser();
        _ready = false;
        _background.Reset();
        _outgoing.Clear();
        _chunkPayload.Clear();
        while (_incoming.TryDequeue(out _)) { }
    }

    private void SuspendBrowser(bool recreate = true)
    {
        ReleaseTextInput();
        if (_view == null) return;
        if (!WebViewCapabilities.NativeLifecycle)
        {
            _view.RemoveBeforeBrowseHandler(BeforeBrowse);
            _view.RemoveResourceRequestHandler(ResourceRequest);
        }
        WebViewLifetime.Close(_view);
        _healthWait = _pingWait = 0;
        _healthFailed = _recovering = false;
        // Older modules need a fresh layout to replace their initial 1x1 texture.
        if (recreate && !WebViewCapabilities.NativeLifecycle && !_shuttingDown)
            _view = CreateView();
    }

    protected override void Dispose(bool disposing)
    {
        ReleaseTextInput();
        _disposed = true;
        _warmCancellation.Cancel();
        _warmCancellation.Dispose();
        if (_view != null)
        {
            _view.RemoveBeforeBrowseHandler(BeforeBrowse);
            _view.RemoveResourceRequestHandler(ResourceRequest);
            WebViewLifetime.Close(_view);
        }
        base.Dispose(disposing);
    }
}

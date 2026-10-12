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
    private const int MaxLogLength = 2048;
    private const int MaxLogPerSecond = 5;
    private WebViewControl _view;
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
    private bool _messageDispatchPending;
    private bool _explicitTextInput;
    private float _healthWait;
    private float _pingWait;
    private bool _healthFailed;
    private bool _recovering;
    private readonly bool _suspendWhenHidden;
    private readonly bool _captureKeyboard;
    // The browser outlives leaving the UI tree (WebViewControl.AlwaysActive): a window that is closed and
    // opened again (the chat composer) keeps its loaded page instead of starting a new browser each time.
    private readonly bool _keepAlive;
    private ISawmill? _pageLog;
    private TimeSpan _logWindow;
    private int _logCount;
    private readonly NativeChatBackground _background;
    public bool BrowserActive => _view.IsInsideTree;
    public bool IsReady => _ready;
    public event Action<string, string>? Message;
    public event Action? Ready;
    public event Action? Failed;

    public void FocusInput() => _view.GrabKeyboardFocus();
    public void SyncInputFocus()
    {
        if (_ready && !_disposed) _view.ExecuteJavaScript("window.DeepLagoonHealth?.syncFocus();");
    }

    public bool HasInputFocus => _view.HasKeyboardFocus();

    public void SendGridEditorKey(string code, bool down, bool control, bool shift, bool alt)
    {
        if (!_ready || _disposed) return;
        var detail = new TguiData().String("code", code).Bool("down", down).Bool("ctrlKey", control)
            .Bool("shiftKey", shift).Bool("altKey", alt).ToString();
        _view.ExecuteJavaScript("window.dispatchEvent(new CustomEvent('grid-editor-key',{detail:" + detail + "}));");
    }

    public void SetWindowDragging(bool dragging)
    {
        if (!_ready || _disposed) return;
        _view.ExecuteJavaScript("window.dispatchEvent(new CustomEvent('deeplagoon/window-drag', {detail:" +
            (dragging ? "true" : "false") + "}));");
    }

    public void FocusTextInput()
    {
        FocusInput();
        _explicitTextInput = true;
        _view.Root?.Window?.TextInputStart();
        if (_ready)
            _view.ExecuteJavaScript("window.focus(); window.dispatchEvent(new Event('deeplagoon/focus-input'));");
    }

    // The current CEF adapter follows Return with a Backspace char event.
    // Composer intercepts that key and sends a DOM keydown without the bad char.
    public void SendInputKey(bool tab, bool shift)
    {
        if (!_ready || _disposed || !HasInputFocus) return;
        _view.ExecuteJavaScript("document.activeElement?.dispatchEvent(new KeyboardEvent('keydown', {" +
            (tab ? "key:'Tab',code:'Tab',keyCode:9,which:9," : "key:'Enter',code:'Enter',keyCode:13,which:13,") +
            "bubbles:true,cancelable:true,shiftKey:" +
            (shift ? "true" : "false") + "}));");
    }

    public void ReleaseTextInput()
    {
        if (_explicitTextInput && HasInputFocus) _view.Root?.Window?.TextInputStop();
        _explicitTextInput = false;
    }

    public void PrepareForShutdown()
    {
        // CEF has already stopped when entity systems shut down. UI teardown
        // (including ending a window drag) must not execute more JavaScript.
        ReleaseTextInput();
        _ready = false;
        _outgoing.Clear();
        while (_incoming.TryDequeue(out _)) { }
    }

    public GameWebView(bool chat = false, bool suspendWhenHidden = false, bool captureKeyboard = false, bool keepAlive = false)
    {
        _captureKeyboard = captureKeyboard;
        _keepAlive = keepAlive && !suspendWhenHidden;
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
        _view = CreateView();
        if (!_suspendWhenHidden) AddChild(_view);
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
        // Starts the browser now, with the handlers already installed.
        if (_keepAlive) view.AlwaysActive = true;
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
        _incoming.Enqueue(uri.Query.TrimStart('?'));
        context.DoRespondStream(new MemoryStream(Convert.FromBase64String(
            "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7")), "image/gif");
    }

    public void Send(string type, string payloadJson)
    {
        if (_disposed) return;
        var json = "{\"type\":" + Quote(type) + ",\"payload\":" + payloadJson + "}";
        if (!_ready)
        {
            if (_outgoing.Count < 256)
                _outgoing.Enqueue(json);
            return;
        }
        Dispatch(json);
    }

    public void Reload()
    {
        if (_disposed || !BrowserActive) return;
        _ready = false;
        _healthWait = _pingWait = 0;
        _healthFailed = false;
        _recovering = false;
        _background?.Reset();
        _outgoing.Clear();
        while (_incoming.TryDequeue(out _)) { }
        _chunkPayload.Clear();
        _view.Reload();
    }

    private void Dispatch(string json) => _view.ExecuteJavaScript("try { window.update(" +
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
        // Attach only once the visible viewport is laid out. Hidden editors do
        // not create background CEF browsers, even while their UI stays in tree.
        if (_suspendWhenHidden && VisibleInTree && _view.Parent == null) AddChild(_view);
        if (!_disposed && BrowserActive && VisibleInTree && !_healthFailed)
        {
            _healthWait += args.DeltaSeconds;
            _pingWait += args.DeltaSeconds;
            if (_ready && _pingWait >= 2)
            {
                _pingWait = 0;
                _view.ExecuteJavaScript("window.DeepLagoonHealth?.ping();");
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
                    _view.ReleaseKeyboardFocus();
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
                    // Force a fresh size notification once CEF has finished creating its view.
                    _view.Arrange(UIBox2.FromDimensions(System.Numerics.Vector2.Zero, Size + System.Numerics.Vector2.One));
                    InvalidateArrange();
                    _ready = true;
                    _healthWait = 0;
                    if (_captureKeyboard) _view.ExecuteJavaScript("window.__deeplagoonCaptureKeys = true;");
                    if (_background != null)
                        _view.ExecuteJavaScript("window.__deeplagoonNativeBackground = true; window.dispatchEvent(new Event('deeplagoon/native-background'));");
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
                case "log":
                    LogPage(payload);
                    break;
                case "native-background":
                    _background?.Configure(payload);
                    break;
                case "chat-input-focus":
                    if (!VisibleInTree || !_view.IsInsideTree ||
                        !TguiActionData.TryParse(payload, out var focus)) break;
                    if (focus!.String("active") == "true")
                    {
                        FocusInput();
                        if (focus.String("text") == "false") ReleaseTextInput();
                        else
                        {
                            _explicitTextInput = true;
                            _view.Root?.Window?.TextInputStart();
                        }
                    }
                    else if (focus.String("active") == "false")
                    {
                        ReleaseTextInput();
                        // Do not release another window's focus (e.g. the composer).
                        _view.ReleaseKeyboardFocus();
                    }
                    break;
                default:
                    Message?.Invoke(type, payload);
                    break;
            }
        }
    }

    protected override void EnteredTree()
    {
        base.EnteredTree();
        // A kept-alive page may have posted messages while its window was closed; they are stale now.
        if (_keepAlive) while (_incoming.TryDequeue(out _)) { }
    }

    // Page warnings and errors (bridge.js forwards TGUI's logger.warn/error and uncaught errors).
    // The page is untrusted: the text is cut, rate limited and logged as a value, not as a template.
    private void LogPage(string payload)
    {
        var now = IoCManager.Resolve<IGameTiming>().RealTime;
        if (now - _logWindow >= TimeSpan.FromSeconds(1))
        {
            _logWindow = now;
            _logCount = 0;
        }
        if (++_logCount > MaxLogPerSecond || !TguiActionData.TryParse(payload, out var entry)) return;
        var ns = entry!.String("ns") ?? "";
        var message = entry.String("message") ?? "";
        if (ns.Length > 64) ns = ns[..64];
        if (message.Length > MaxLogLength) message = message[..MaxLogLength];
        _pageLog ??= IoCManager.Resolve<ILogManager>().GetSawmill("tgui.page");
        if (entry.String("level") == "error")
            _pageLog.Error("{Document} [{Namespace}] {Message}", _documentUrl, ns, message);
        else
            _pageLog.Warning("{Document} [{Namespace}] {Message}", _documentUrl, ns, message);
    }

    protected override void ExitedTree()
    {
        base.ExitedTree();
        if (_keepAlive && !_disposed)
        {
            // The page stays loaded and ready. Drop its field focus so a caret does not repaint it while hidden.
            if (_ready) _view.ExecuteJavaScript("document.activeElement?.blur();");
            _healthWait = _pingWait = 0;
            while (_incoming.TryDequeue(out _)) { }
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
        if (!_suspendWhenHidden || VisibleInTree || _view.Parent == null) return;
        SuspendBrowser();
        _ready = false;
        _background?.Reset();
        _outgoing.Clear();
        while (_incoming.TryDequeue(out _)) { }
    }

    private void SuspendBrowser()
    {
        ReleaseTextInput();
        _view.RemoveBeforeBrowseHandler(BeforeBrowse);
        _view.RemoveResourceRequestHandler(ResourceRequest);
        WebViewLifetime.Close(_view);
        _healthWait = _pingWait = 0;
        _healthFailed = _recovering = false;
        // StartBrowser allocates a 1x1 texture. A fresh control guarantees that
        // layout invokes Resized even when the resumed viewport has the same size.
        _view = CreateView();
    }

    protected override void Dispose(bool disposing)
    {
        ReleaseTextInput();
        _disposed = true;
        _view.RemoveBeforeBrowseHandler(BeforeBrowse);
        _view.RemoveResourceRequestHandler(ResourceRequest);
        // A kept-alive browser outside the tree is only closed by turning AlwaysActive off.
        if (_keepAlive) _view.AlwaysActive = false;
        WebViewLifetime.Close(_view);
        base.Dispose(disposing);
    }
}

using System.IO;
using System.Numerics;
using System.Threading;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Lobby;
using Content.Shared.CCVar;
using Robust.Client.ResourceManagement;
using Robust.Client.WebView;
using Robust.Shared.Configuration;
using Robust.Shared.Log;

namespace Content.Client.Lobby.UI;

public sealed partial class LobbyBackground
{
    public const string DefaultBackground = "/Textures/_Mono/LobbyScreens/colossus_central_render.webp";
    private const string Document = "res://deeplagoon/Web/DeepLagoon/lobby-background.html";
    private const string Script = "res://deeplagoon/Web/DeepLagoon/lobby-background.js";
    private const string Status = "res://deeplagoon/Web/DeepLagoon/lobby-background-status";
    private static readonly byte[] Ack = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
    private WebViewControl? _mediaView;
    private Action<IRequestHandlerContext>? _requestHandler;
    private Action<IBeforeBrowseContext>? _browseHandler;
    private string? _source;
    private bool _active;
    private bool _failed;
    private bool _shutdown;
    private bool _ready;
    private float _loadWait;
    private int _status;
    public bool MediaBrowserActive => _mediaView?.IsInsideTree == true;

    public void Start()
    {
        _active = !_shutdown;
        _failed = false;
    }

    public void Stop()
    {
        _active = false;
        CloseMedia();
    }

    public void SetSource(string? source)
    {
        if (!LobbyBackgroundMedia.IsSupported(source))
            source = null;
        if (_source == source)
            return;
        CloseMedia();
        _source = source;
        _failed = false;
        var cache = IoCManager.Resolve<IResourceCache>();
        var still = source == null || LobbyBackgroundMedia.IsAnimated(source) ? DefaultBackground : source;
        Texture = cache.TryGetResource<TextureResource>(still, out var texture) ? texture.Texture : null;
    }

    private void UpdateMedia(float delta)
    {
        if (_shutdown || !_active || !VisibleInTree || _source == null ||
            !LobbyBackgroundMedia.IsAnimated(_source) ||
            !IoCManager.Resolve<IConfigurationManager>().GetCVar(CCVars.LobbyAnimatedBackground))
        {
            CloseMedia();
            return;
        }
        if (_failed)
            return;
        if (_mediaView == null)
        {
            try { OpenMedia(_source); }
            catch (Exception e)
            {
                _failed = true;
                CloseMedia();
                Logger.GetSawmill("lobby.background").Warning($"Animated background unavailable: {e.Message}");
            }
            return;
        }

        var status = Interlocked.Exchange(ref _status, 0);
        if (status == 1)
        {
            _ready = true;
            _mediaView.Modulate = Color.White;
            // Stock WebView starts with a 1x1 texture. Request a fresh layout after its first frame.
            _mediaView.Arrange(UIBox2.FromDimensions(Vector2.Zero, Size + Vector2.One));
            InvalidateArrange();
        }
        if (!_ready)
            _loadWait += delta;
        if (status == 2 || _loadWait > 20)
        {
            _failed = true;
            CloseMedia();
            Logger.GetSawmill("lobby.background").Warning("Animated background failed to load; keeping the static background.");
        }
    }

    private void OpenMedia(string source)
    {
        var mediaUrl = new Uri("res://deeplagoon" + source).AbsoluteUri;
        var documentUrl = Document + "?src=" + Uri.EscapeDataString(mediaUrl);
        var view = new WebViewControl
        {
            HorizontalExpand = true,
            VerticalExpand = true,
            MouseFilter = MouseFilterMode.Ignore,
            CanKeyboardFocus = false,
            KeyboardFocusOnClick = false,
            AlwaysActive = false,
            Modulate = Color.Transparent,
            Url = documentUrl,
        };
        _mediaView = view;
        _browseHandler = context => { if (context.Url != documentUrl) context.DoCancel(); };
        _requestHandler = context =>
        {
            if (context.IsDownload || context.Method != "GET") { context.DoCancel(); return; }
            if (context.Url == Status + "?ready" || context.Url == Status + "?error")
            {
                if (ReferenceEquals(_mediaView, view))
                    Interlocked.Exchange(ref _status, context.Url.EndsWith("?ready", StringComparison.Ordinal) ? 1 : 2);
                context.DoRespondStream(new MemoryStream(Ack, writable: false), "image/gif");
                return;
            }
            if (context.Url != documentUrl && context.Url != Script && context.Url != mediaUrl)
                context.DoCancel();
        };
        view.AddBeforeBrowseHandler(_browseHandler);
        view.AddResourceRequestHandler(_requestHandler);
        WebViewLifetime.Register(view, () => { _shutdown = true; Stop(); });
        AddChild(view);
    }

    private void CloseMedia()
    {
        var view = _mediaView;
        if (view == null)
            return;
        _mediaView = null;
        _ready = false;
        _loadWait = 0;
        Interlocked.Exchange(ref _status, 0);
        if (_requestHandler != null) view.RemoveResourceRequestHandler(_requestHandler);
        if (_browseHandler != null) view.RemoveBeforeBrowseHandler(_browseHandler);
        _requestHandler = null;
        _browseHandler = null;
        // Close through the official UI lifecycle, with no warm browser or expiry timer.
        WebViewLifetime.Close(view);
    }

    protected override void VisibilityChanged(bool newVisible)
    {
        base.VisibilityChanged(newVisible);
        if (!VisibleInTree)
            CloseMedia();
    }

    protected override void ExitedTree()
    {
        base.ExitedTree();
        _active = false;
        // The parent is already outside the tree but its children have not received
        // ExitedTree yet. Let the engine close the child, then discard its managed owner.
        UserInterfaceManager.DeferAction(() => { if (!IsInsideTree) CloseMedia(); });
    }

    protected override void Dispose(bool disposing)
    {
        Stop();
        base.Dispose(disposing);
    }
}

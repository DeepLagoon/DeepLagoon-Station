using System.IO;
using System.Numerics;
using Robust.Client.UserInterface.Controls;
using Robust.Client.WebView;
using Robust.Shared.ContentPack;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>The same restricted wiki viewer is used by ordinary help and mandatory rules.</summary>
public sealed class WikiDocumentView : BoxContainer
{
    private readonly WikiRoutingConfiguration _configuration = IoCManager.Resolve<WikiRoutingConfiguration>();
    private readonly WebViewControl _web = new() { HorizontalExpand = true, VerticalExpand = true };
    private readonly Label _status = new();
    private readonly HashSet<string> _paths = new();
    private readonly string _treeScript;
    private string _origin = "";
    private string _treeRoot = "";
    private string _treeLocale = "";
    private float _loadSeconds;
    private float _treeRefresh;
    private bool _ensureViewport;
    private bool _shuttingDown;

    public WikiDocumentView()
    {
        Orientation = LayoutOrientation.Vertical;
        HorizontalExpand = VerticalExpand = true;
        WebViewLifetime.Register(_web, () => _shuttingDown = true);
        using (var stream = IoCManager.Resolve<IResourceManager>().ContentFileRead(new ResPath("/Web/DeepLagoon/wiki-tree.js")))
        using (var reader = new StreamReader(stream)) _treeScript = reader.ReadToEnd();
        _web.AddBeforeBrowseHandler(BeforeBrowse);
        _web.AddResourceRequestHandler(ResourceRequest);
        AddChild(_status);
        AddChild(_web);
    }

    public void LoadPath(string path)
    {
        _origin = _configuration.Origin;
        _treeRoot = _configuration.TreeRoot;
        _treeLocale = _configuration.TreeLocale;
        _paths.Clear();
        var routes = _configuration.Read();
        foreach (var key in routes.Routes.Keys)
        {
            var destination = routes.Resolve(new[] { key });
            if (WikiNavigationPolicy.IsPath(destination)) _paths.Add(destination);
        }
        if (!WikiNavigationPolicy.IsOrigin(_origin) || !WikiNavigationPolicy.IsPath(path))
        {
            _web.Visible = false;
            _status.Text = Loc.GetString("wiki-page-unmapped");
            return;
        }
        _paths.Add(path);
        _web.Visible = true;
        _ensureViewport = true;
        _loadSeconds = 0;
        _treeRefresh = 0;
        _status.Text = Loc.GetString("wiki-loading");
        _web.Url = _origin + path;
    }

    public void Reload() { _loadSeconds = 0; _web.Reload(); }

    private void BeforeBrowse(IBeforeBrowseContext context)
    {
        if (context.Method != "GET" || !WikiNavigationPolicy.IsPage(context.Url, _paths, _origin, _treeRoot))
            context.DoCancel();
    }

    private void ResourceRequest(IRequestHandlerContext context)
    {
        if (!context.IsDownload && !context.IsNavigation && context.Method == "POST" &&
            WikiNavigationPolicy.IsTreeRequest(context.Url, _origin)) return;
        if (context.IsDownload || context.Method != "GET" ||
            (context.IsNavigation ? !WikiNavigationPolicy.IsPage(context.Url, _paths, _origin, _treeRoot) :
                !WikiNavigationPolicy.IsAsset(context.Url, _origin) &&
                !WikiNavigationPolicy.IsPage(context.Url, _paths, _origin, _treeRoot)))
            context.DoCancel();
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (_shuttingDown || !_web.Visible) return;
        _loadSeconds += args.DeltaSeconds;
        if (!WebViewCapabilities.NativeLifecycle && _ensureViewport && !_web.IsLoading && _loadSeconds >= 1)
        {
            _web.Arrange(UIBox2.FromDimensions(Vector2.Zero, _web.Size + Vector2.One));
            InvalidateArrange();
            _ensureViewport = false;
        }
        _treeRefresh -= args.DeltaSeconds;
        if (!_web.IsLoading && _treeRefresh <= 0)
        {
            _treeRefresh = 1;
            _web.ExecuteJavaScript("window.__deepLagoonWikiConfig=" + new TguiData()
                .String("root", _treeRoot).String("locale", _treeLocale) + ";" + _treeScript);
        }
        _status.Text = Loc.GetString(_web.IsLoading
            ? (_loadSeconds >= 30 ? "wiki-load-timeout" : "wiki-loading") : "wiki-load-hint");
    }

    protected override void Dispose(bool disposing)
    {
        _web.RemoveBeforeBrowseHandler(BeforeBrowse);
        _web.RemoveResourceRequestHandler(ResourceRequest);
        WebViewLifetime.Close(_web);
        base.Dispose(disposing);
    }
}

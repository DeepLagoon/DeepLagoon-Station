using System.Numerics;
using Content.Client.UserInterface.Controls;
using Content.Client.UserInterface.Systems.Guidebook;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.WebUI;

public sealed class WikiGuidebookWindow : FancyWindow
{
    private readonly WikiRoutingConfiguration _configuration = IoCManager.Resolve<WikiRoutingConfiguration>();
    private readonly WikiDocumentView _document = new();
    private readonly BoxContainer _toolbar = new();

    public WikiGuidebookWindow()
    {
        Title = Loc.GetString("wiki-window-title");
        SetSize = new Vector2(1000, 750);
        MinSize = new Vector2(400, 300);
        var body = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };
        body.AddChild(_toolbar);
        body.AddChild(_document);
        ContentsContainer.AddChild(body);
    }

    public void ShowPath(string path)
    {
        _toolbar.RemoveAllChildren();
        var prototypes = IoCManager.Resolve<IPrototypeManager>();
        var routes = _configuration.Read();
        foreach (var key in routes.Routes.Keys)
        {
            if (!key.StartsWith("page:", StringComparison.Ordinal) || routes.Resolve(new[] { key }).Length == 0) continue;
            var id = key[5..];
            var button = new Button { Text = prototypes.TryIndex<WikiPagePrototype>(id, out var page) ? Loc.GetString(page.Name) : id };
            button.OnPressed += _ => UserInterfaceManager.GetUIController<GuidebookUIController>().OpenSource(key);
            _toolbar.AddChild(button);
        }
        var reload = new Button { Text = Loc.GetString("wiki-reload") };
        reload.OnPressed += _ => _document.Reload();
        _toolbar.AddChild(reload);
        _document.LoadPath(path);
    }
}

using Content.Client._DeepLagoon.WebUI;
using Content.Client.Gameplay;
using Content.Client.Guidebook;
using Content.Client.Guidebook.Controls;
using System.Linq;
using Content.Client.Lobby;
using Content.Client.Players.PlayTimeTracking;
using Content.Client.UserInterface.Controls;
using Content.Shared.Guidebook;
using Content.Shared.Input;
using Robust.Client.UserInterface.Controllers;
using Robust.Client.UserInterface;
using Robust.Shared.Input.Binding;
using Robust.Shared.Prototypes;
using static Robust.Client.UserInterface.Controls.BaseButton;

namespace Content.Client.UserInterface.Systems.Guidebook;

public sealed partial class GuidebookUIController : UIController, IOnStateEntered<LobbyState>, IOnStateEntered<GameplayState>, IOnStateExited<LobbyState>, IOnStateExited<GameplayState>, IOnSystemChanged<GuidebookSystem>
{
    [UISystemDependency] private readonly GuidebookSystem _guidebookSystem = default!;
    [Dependency] private JobRequirementsManager _jobRequirements = default!;
    [Dependency] private WikiRoutingConfiguration _routing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IUriOpener _uri = default!;
    private GuidebookWindow? _localWindow;
    private WikiGuidebookWindow? _guideWindow;
    private MenuButton? GuidebookButton => UIManager.GetActiveUIWidgetOrNull<MenuBar.Widgets.GameTopMenuBar>()?.GuidebookButton;

    public void OnStateEntered(LobbyState state) => Enter(state is LobbyState);
    public void OnStateEntered(GameplayState state) => Enter(false);
    private void Enter(bool lobby)
    {
        _guideWindow = UIManager.CreateWindow<WikiGuidebookWindow>();
        _guideWindow.OnOpen += OnWindowOpen;
        _guideWindow.OnClose += OnWindowClosed;
        CommandBinds.Builder.Bind(ContentKeyFunctions.OpenGuidebook,
            InputCmdHandler.FromDelegate(_ => ToggleGuidebook("button:key-guidebook"))).Register<GuidebookUIController>();
        if (lobby && _jobRequirements.FetchOverallPlaytime() < TimeSpan.FromMinutes(Math.Max(0, _routing.NewPlayerMinutes)))
            OpenSource("button:new-player");
    }
    public void OnStateExited(LobbyState state) => Exit();
    public void OnStateExited(GameplayState state) => Exit();
    private void Exit()
    {
        if (_guideWindow != null)
        {
            _guideWindow.OnOpen -= OnWindowOpen;
            _guideWindow.OnClose -= OnWindowClosed;
            _guideWindow.Dispose();
            _guideWindow = null;
        }
        _localWindow?.Dispose();
        _localWindow = null;
        CommandBinds.Unregister<GuidebookUIController>();
    }
    public void OnSystemLoaded(GuidebookSystem system) => system.OnGuidebookOpen += OpenGuidebook;
    public void OnSystemUnloaded(GuidebookSystem system) => system.OnGuidebookOpen -= OpenGuidebook;
    internal void UnloadButton()
    {
        if (GuidebookButton != null) GuidebookButton.OnPressed -= GuidebookButtonOnPressed;
    }
    internal void LoadButton()
    {
        if (GuidebookButton != null) GuidebookButton.OnPressed += GuidebookButtonOnPressed;
    }
    private void GuidebookButtonOnPressed(ButtonEventArgs args) => ToggleGuidebook("button:top-guidebook");
    private void OnWindowOpen()
    {
        if (GuidebookButton != null) GuidebookButton.Pressed = true;
    }
    private void OnWindowClosed()
    {
        if (GuidebookButton != null) GuidebookButton.Pressed = false;
    }
    public void ToggleGuidebook(string source = "button:guidebook")
    {
        if (_guideWindow?.IsOpen == true || _localWindow?.IsOpen == true) CloseGuidebook();
        else OpenSource(source);
    }
    // Keep the existing API used by help verbs, rules, and the guidebook keybinding.
    // Documents are now resolved exclusively through explicit wikiPage mappings.
    public void OpenGuidebook(
        Dictionary<ProtoId<GuideEntryPrototype>, GuideEntry>? guides = null,
        List<ProtoId<GuideEntryPrototype>>? rootEntries = null,
        ProtoId<GuideEntryPrototype>? forceRoot = null,
        bool includeChildren = true,
        ProtoId<GuideEntryPrototype>? selected = null)
    {
        if (_guideWindow == null) return;
        var guide = selected ?? forceRoot;
        if (guide == null && guides?.Count == 1)
            foreach (var id in guides.Keys) guide = id;
        OpenSource(guide == null ? "button:guidebook" : "guide:" + guide);
    }
    public void OpenGuidebook(
        List<ProtoId<GuideEntryPrototype>> guideList,
        List<ProtoId<GuideEntryPrototype>>? rootEntries = null,
        ProtoId<GuideEntryPrototype>? forceRoot = null,
        bool includeChildren = true,
        ProtoId<GuideEntryPrototype>? selected = null)
    {
        OpenSource("button:guidebook", guideList.Select(id => id.ToString()), selected ?? forceRoot);
    }
    public void OpenWikiPage(string pageId) => OpenSource("page:" + pageId);

    public bool HasSource(string source) => _routing.Read().Contains(source);

    public bool OpenSource(string source, IEnumerable<string>? guides = null,
        ProtoId<GuideEntryPrototype>? selected = null, string? fallback = null)
    {
        var sources = new List<string> { source };
        if (selected != null) sources.Add("guide:" + selected);
        if (guides != null) sources.AddRange(guides.Select(id => "guide:" + id));
        // Generic help buttons must not outrank a specific guide mapping.
        if (source == "button:guidebook" && sources.Count > 1) sources.RemoveAt(0);
        return OpenDestination(_routing.Read().Resolve(sources, fallback ?? "default"));
    }

    private List<string> EntitySources(EntityUid entity, string prefix)
    {
        var sources = new List<string>();
        var prototype = _entities.GetComponent<MetaDataComponent>(entity).EntityPrototype;
        if (prototype != null)
        {
            sources.Add(prefix + ":" + prototype.ID);
            foreach (var (id, _) in _prototypes.EnumerateAllParents<EntityPrototype>(prototype.ID))
                sources.Add(prefix + ":" + id);
        }
        return sources;
    }

    public bool HasEntityRoute(EntityUid entity, string prefix = "entity")
    {
        var routes = _routing.Read();
        return EntitySources(entity, prefix).Any(routes.Contains);
    }

    public bool OpenEntityHelp(EntityUid entity, IEnumerable<string>? guides = null, string prefix = "entity")
    {
        var sources = EntitySources(entity, prefix);
        if (guides != null) sources.AddRange(guides.Select(id => "guide:" + id));
        return OpenDestination(_routing.Read().Resolve(sources));
    }

    private bool OpenDestination(string destination)
    {
        if (destination.StartsWith("external:", StringComparison.Ordinal))
        {
            var url = destination[9..];
            if (!WikiNavigationPolicy.TryUri(url, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0) return false;
            _uri.OpenUri(url);
            return true;
        }
        if (destination.StartsWith("local:", StringComparison.Ordinal))
        {
            var id = destination[6..];
            if (!_prototypes.HasIndex<GuideEntryPrototype>(id)) return false;
            _guideWindow?.Close();
            if (_localWindow == null)
            {
                _localWindow = UIManager.CreateWindow<GuidebookWindow>();
                _localWindow.OnOpen += OnWindowOpen;
                _localWindow.OnClose += OnWindowClosed;
            }
            var entries = _prototypes.EnumeratePrototypes<GuideEntryPrototype>()
                .ToDictionary(p => (ProtoId<GuideEntryPrototype>) p.ID, p => (GuideEntry) p);
            _localWindow.UpdateGuides(entries, rootEntries: new() { id }, selected: id);
            _localWindow.OpenCenteredRight();
            return true;
        }
        if (_guideWindow == null || !WikiNavigationPolicy.IsPath(destination) || !WikiNavigationPolicy.IsOrigin(_routing.Origin)) return false;
        _localWindow?.Close();
        _guideWindow.ShowPath(destination);
        _guideWindow.OpenCenteredRight();
        return true;
    }

    public void CloseGuidebook()
    {
        _guideWindow?.Close();
        _localWindow?.Close();
    }
}

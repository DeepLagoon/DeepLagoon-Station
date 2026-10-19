using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared.Access.Systems;
using Content.Shared.Research.Components;
using Content.Shared.Research.Prototypes;
using JetBrains.Annotations;
using Robust.Client.GameObjects;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Content.Client.Research.UI;

namespace Content.Client._DeepLagoon.Research;

/// <summary>
/// TGUI replacement for the native R&amp;D console window. It reuses the existing console state and
/// messages, so the server and the network protocol are unchanged.
/// </summary>
[UsedImplicitly]
public sealed class ResearchConsoleTguiBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private const string InterfaceName = "ResearchConsole";
    private const int MaxIconRequest = 128;

    private ResearchConsoleTguiWindow? _window;
    private ResearchConsoleMenu? _classicWindow;
    private bool _classicForSession;
    private ResearchConsoleTguiData? _data;
    private string _title = string.Empty;
    private string _staticJson = string.Empty;
    private string? _staticKey;
    private HashSet<string> _knownTechnologies = new();
    private HashSet<string> _knownRecipes = new();

    protected override void Open()
    {
        base.Open();
        if (_classicForSession || !WindowInterfaceStyle.UseTgui(InterfaceName))
        {
            OpenClassic();
            return;
        }

        _data = new ResearchConsoleTguiData(EntMan);
        _title = Loc.GetString("research-console-menu-title");

        _window = new ResearchConsoleTguiWindow { Title = _title };
        _window.OnClose += Close;
        WindowInterfaceStyle.OpenPresentation(this, _window, _classicWindow);
        _window.Panel.OnClose += Close;
        _window.Panel.OnAction += OnAction;
        // The panel publishes its own state first on every (re)load; static data follows it.
        _window.Panel.Web.Ready += SendStatic;
        _window.Panel.Web.Failed += SwitchToClassic;
        _window.Panel.BindStyle(_window, InterfaceName, () => true, SwitchInterface);
        _window.Panel.SetNativeFallback(SwitchToClassic);
        _window.Panel.SetState(InterfaceName, "{}", _title);
        PushState();
    }

    public override void OnProtoReload(PrototypesReloadedEventArgs args)
    {
        base.OnProtoReload(args);

        if (!args.WasModified<TechnologyPrototype>())
            return;

        ResearchConsoleTguiData.ClearCache();
        _staticKey = null;
        PushState();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        PushState();
        if (state is ResearchConsoleBoundInterfaceState research)
        {
            _classicWindow?.UpdatePanels(research);
            _classicWindow?.UpdateInformationPanel(research);
        }
    }

    private void SwitchToClassic()
    {
        if (_window is not { } window || !IsOpened) return;
        _window = null;
        _classicForSession = true;
        WindowInterfaceStyle.ClosePresentation(this, window);
#pragma warning disable CS0618
        window.Dispose();
#pragma warning restore CS0618
        OpenClassic();
    }

    private void SwitchInterface(bool tgui)
    {
        if (!IsOpened) return;
        if (!tgui) { SwitchToClassic(); return; }
        if (_classicWindow is not { } native) return;
        WindowInterfaceStyle.HideForSwitch(this, native);
        _classicForSession = false;
        _staticKey = null;
        Open();
    }

    private void OpenClassic()
    {
        // The engine registers one native control per BUI until the BUI closes.
        // Switching presentation must reuse it, including its event subscriptions.
        if (_classicWindow != null)
        {
            _classicWindow.Open(_classicWindow.Position);
            if (State != null) UpdateState(State);
            return;
        }

        _classicWindow = this.CreateWindow<ResearchConsoleMenu>();
        WindowInterfaceStyle.Attach(_classicWindow, InterfaceName, () => false, next =>
        {
            WindowInterfaceStyle.Save(InterfaceName, next);
            SwitchInterface(next);
        });
        _classicWindow.SetEntity(Owner);
        _classicWindow.OnTechnologyCardPressed += id => SendMessage(new ConsoleUnlockTechnologyMessage(id));
        _classicWindow.OnServerButtonPressed += () => SendMessage(new ConsoleServerSelectionMessage());
        if (State != null) UpdateState(State);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _window is { } window)
        {
            _window = null;
            WindowInterfaceStyle.ClosePresentation(this, window);
            // GameWebView owns the asynchronous browser teardown.
#pragma warning disable CS0618
            if (!window.Disposed) window.Dispose();
#pragma warning restore CS0618
        }

        base.Dispose(disposing);
    }

    private void PushState()
    {
        if (_window == null || _data == null || State is not ResearchConsoleBoundInterfaceState state)
            return;

        var staticKey = string.Join(',', state.Researches.Keys.OrderBy(id => id, StringComparer.Ordinal));
        if (staticKey != _staticKey)
        {
            _staticKey = staticKey;
            _knownTechnologies = new HashSet<string>(state.Researches.Keys);
            _knownRecipes = _data.RecipeIds(_knownTechnologies);
            _staticJson = _data.BuildStatic(_knownTechnologies, staticKey);
            SendStatic();
        }

        _window.Panel.SetState(InterfaceName, _data.BuildDynamic(state, HasAccess()), _title);
    }

    private void SendStatic()
    {
        if (_window is { Panel.Web.IsReady: true } && _staticJson.Length > 0)
            _window.Panel.Web.Send("update", _staticJson);
    }

    private bool HasAccess()
    {
        var player = IoCManager.Resolve<IPlayerManager>();
        return player.LocalEntity is { } entity && EntMan.System<AccessReaderSystem>().IsAllowed(entity, Owner);
    }

    private void OnAction(string action, string payload)
    {
        switch (action)
        {
            case "research":
                if (TguiActionData.TryParse(payload, out var data) &&
                    data?.String("id") is { } id &&
                    _knownTechnologies.Contains(id))
                {
                    SendMessage(new ConsoleUnlockTechnologyMessage(id));
                }
                break;
            case "servers":
                SendMessage(new ConsoleServerSelectionMessage());
                break;
            case "icons":
                SendIcons(payload);
                break;
        }
    }

    private void SendIcons(string payload)
    {
        if (_window == null || _data == null ||
            !TguiActionData.TryParse(payload, out var data) || data?.String("ids") is not { } ids)
        {
            return;
        }

        var requested = ids.Split(',')
            .Where(IsKnownIcon)
            .Distinct()
            .Take(MaxIconRequest)
            .ToList();
        if (requested.Count > 0)
            _window.Panel.Web.Send("update", _data.BuildIcons(requested));
    }

    private bool IsKnownIcon(string key)
    {
        if (!key.StartsWith(ResearchConsoleTguiData.RecipeIconPrefix, StringComparison.Ordinal))
            return _knownTechnologies.Contains(key);

        return _knownRecipes.Contains(key[ResearchConsoleTguiData.RecipeIconPrefix.Length..]);
    }
}

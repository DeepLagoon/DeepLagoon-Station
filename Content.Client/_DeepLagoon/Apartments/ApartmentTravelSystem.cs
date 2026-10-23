using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Controls;
using Content.Shared._DeepLagoon.Apartments;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Apartments;

public sealed partial class ApartmentTravelSystem : EntitySystem
{
    private ApartmentTravelWindow? _window;
    private ApartmentTravelStateEvent? _state;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<ApartmentTravelStateEvent>(OnState);
        SubscribeNetworkEvent<ApartmentTravelClosedEvent>(_ => Close(false));
    }

    public override void Shutdown() { Close(false); base.Shutdown(); }

    private void OnState(ApartmentTravelStateEvent state)
    {
        _state = state;
        if (_window == null)
        {
            _window = new ApartmentTravelWindow();
            _window.OnClose += () => Close(true);
            _window.Panel.OnClose += () => Close(true);
            _window.Panel.OnAction += OnAction;
            _window.OpenCentered();
        }
        TguiData Person(ApartmentTravelPerson person) => new TguiData().String("user", person.User.ToString())
            .String("name", person.Name).Bool("invited", person.Invited).Bool("inside", person.Inside).Number("seconds", person.Seconds);
        _window.Title = state.AtHome ? "Терминал квартиры" : "Жилой маршрут Lagoon";
        _window.Panel.SetState(state.AtHome ? "ApartmentTerminal" : "ApartmentTravel", new TguiData().Bool("atHome", state.AtHome).Bool("canManage", state.CanManage)
            .Bool("entryOpen", state.EntryOpen).Bool("canDepart", state.CanDepart).Bool("pending", state.Pending)
            .String("message", state.Message).Array("people", state.People.Select(Person)).Array("invitations", state.Invitations.Select(Person)).ToString(), _window.Title);
    }

    private void OnAction(string action, string payload)
    {
        if (_state == null) return;
        if (action == "shop") { EntityManager.System<ApartmentShopSystem>().Open(); return; }
        if (action == "close") { Close(true); return; }
        if (action == "focus") { _window?.Panel.Web.FocusTextInput(); return; }
        if (!Enum.TryParse<ApartmentTravelAction>(action, true, out var operation) || !Enum.IsDefined(operation)) return;
        if (!TguiActionData.TryParse(payload, out var fields, 1024)) return;
        var target = Guid.TryParse(fields!.String("target"), out var user) ? user : Guid.Empty;
        RaiseNetworkEvent(new ApartmentTravelActionEvent(_state.Token, operation, target));
    }

    private void Close(bool notify)
    {
        var window = _window;
        var state = _state;
        _window = null; _state = null;
        if (notify && state != null) RaiseNetworkEvent(new ApartmentTravelActionEvent(state.Token, ApartmentTravelAction.Close));
        if (window == null) return;
        window.Panel.Web.ReleaseTextInput();
        window.Close();
        Timer.Spawn(3000, () =>
        {
#pragma warning disable CS0618
            if (!window.Disposed) window.Dispose();
#pragma warning restore CS0618
        });
    }
}

public sealed class ApartmentTravelWindow : FancyWindow
{
    public readonly TguiPanel Panel = new();
    public ApartmentTravelWindow()
    {
        Title = "Жилой маршрут Lagoon";
        SetSize = new Vector2(740, 600);
        MinSize = new Vector2(520, 420);
        ContentsContainer.AddChild(Panel);
    }
}

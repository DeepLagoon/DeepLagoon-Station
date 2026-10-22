using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Apartments;

namespace Content.Client._DeepLagoon.Apartments;

public sealed partial class ApartmentElevatorSystem : EntitySystem
{
    [Dependency] private Robust.Client.Graphics.IOverlayManager _overlays = default!;
    [Dependency] private Robust.Shared.Timing.IGameTiming _timing = default!;
    private ApartmentElevatorCurtain _curtain = default!;
    private ApartmentTravelWindow? _window;
    private ApartmentElevatorStateEvent? _state;
    public override void Initialize()
    {
        base.Initialize();
        _curtain = new ApartmentElevatorCurtain(_timing);
        _overlays.AddOverlay(_curtain);
        SubscribeNetworkEvent<ApartmentElevatorCurtainEvent>(ev => _curtain.Show(ev.Duration));
        SubscribeNetworkEvent<ApartmentElevatorStateEvent>(OnState);
        SubscribeNetworkEvent<ApartmentElevatorClosedEvent>(_ => Close());
    }
    public override void Shutdown() { Close(); _overlays.RemoveOverlay(_curtain); base.Shutdown(); }

    private void OnState(ApartmentElevatorStateEvent state)
    {
        _state = state;
        if (_window == null)
        {
            _window = new ApartmentTravelWindow();
            _window.OnClose += Close;
            _window.Panel.OnClose += Close;
            _window.Panel.OnAction += OnAction;
            _window.OpenCentered();
        }
        _window.Title = "Лифт жилого комплекса";
        _window.Panel.SetState("ApartmentElevator", new TguiData().Number("number", state.Number)
            .Bool("atHome", state.AtHome).Bool("busy", state.Busy).String("message", state.Message).ToString(), _window.Title);
    }

    private void OnAction(string action, string payload)
    {
        if (action == "focus") { _window?.Panel.Web.FocusTextInput(); return; }
        if (_state == null) return;
        if (action == "close") { Close(); return; }
        if (action is not ("depart" or "return") || !TguiActionData.TryParse(payload, out var fields, 256)) return;
        var number = int.TryParse(fields!.String("number"), out var parsed) ? parsed : 0;
        RaiseNetworkEvent(new ApartmentElevatorActionEvent(_state.Token, action, number));
    }

    private void Close()
    {
        var window = _window;
        _window = null;
        if (_state != null) RaiseNetworkEvent(new ApartmentElevatorActionEvent(_state.Token, "close"));
        _state = null;
        if (window == null) return;
        window.Panel.Web.ReleaseTextInput();
        window.Close();
        Robust.Shared.Timing.Timer.Spawn(3000, () =>
        {
#pragma warning disable CS0618
            if (!window.Disposed) window.Dispose();
#pragma warning restore CS0618
        });
    }
}

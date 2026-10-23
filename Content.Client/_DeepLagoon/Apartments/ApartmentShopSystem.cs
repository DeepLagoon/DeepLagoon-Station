using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Apartments;

namespace Content.Client._DeepLagoon.Apartments;

public sealed class ApartmentShopSystem : EntitySystem
{
    private ApartmentTravelWindow? _window;
    private ApartmentShopState? _state;
    public override void Initialize() => SubscribeNetworkEvent<ApartmentShopState>(OnState);
    public override void Shutdown() { Close(); base.Shutdown(); }
    public void Open() => RaiseNetworkEvent(new ApartmentShopRequest());
    private void OnState(ApartmentShopState state)
    {
        _state = state;
        if (_window == null)
        {
            _window = new ApartmentTravelWindow();
            _window.OnClose += Close; _window.Panel.OnClose += Close;
            _window.Panel.OnAction += Action; _window.OpenCentered();
        }
        _window.Title = "Шаблоны квартир";
        _window.Panel.SetState("ApartmentShop", new TguiData().Number("number", state.Number).Bool("lobby", state.Lobby)
            .Bool("legacy", state.Legacy).String("message", state.Message).Array("templates", state.Templates.Select(item =>
                new TguiData().String("id", item.Id).String("name", item.Name).Number("price", item.Price)
                    .Bool("owned", item.Owned).Bool("selected", item.Selected))).ToString(), _window.Title);
    }
    private void Action(string action, string payload)
    {
        if (_state == null) return;
        if (action == "close") { Close(); return; }
        if (!TguiActionData.TryParse(payload, out var fields, 1024)) return;
        RaiseNetworkEvent(new ApartmentShopAction(_state.Token, action, fields!.String("template") ?? ""));
    }
    private void Close()
    {
        var window = _window; _window = null;
        if (_state != null) RaiseNetworkEvent(new ApartmentShopAction(_state.Token, "close"));
        _state = null;
        if (window == null) return;
        window.Close(); window.Panel.Web.ReleaseTextInput();
        Robust.Shared.Timing.Timer.Spawn(3000, () => {
#pragma warning disable CS0618
            if (!window.Disposed) window.Dispose();
#pragma warning restore CS0618
        });
    }
}

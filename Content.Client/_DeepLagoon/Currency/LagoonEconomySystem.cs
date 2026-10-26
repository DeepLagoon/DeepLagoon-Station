using Content.Client._DeepLagoon.Apartments;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Currency;
using Robust.Client;
using Robust.Shared.Network;

namespace Content.Client._DeepLagoon.Currency;

public sealed class LagoonEconomySystem : EntitySystem
{
    [Dependency] private IBaseClient _client = default!;
    private ApartmentTravelWindow? _window;
    private Guid _token, _request;
    private bool _loading;
    public string[] PaidNames { get; private set; } = [];
    public bool PaidNamesLoaded { get; private set; }
    public event Action? PaidNamesChanged;
    public override void Initialize()
    {
        SubscribeNetworkEvent<LagoonEconomyStateEvent>(OnState);
        SubscribeNetworkEvent<LagoonPaidSupportersEvent>(ev => { PaidNames = ev.Names; PaidNamesLoaded = true; PaidNamesChanged?.Invoke(); });
        _client.RunLevelChanged += OnRunLevel;
    }
    public override void Shutdown() { _client.RunLevelChanged -= OnRunLevel; Close(); base.Shutdown(); }
    private void OnRunLevel(object? sender, RunLevelChangedEventArgs args)
    {
        if (args.NewLevel != ClientRunLevel.Initialize) return;
        Close(); PaidNames = []; PaidNamesLoaded = false; PaidNamesChanged?.Invoke();
    }
    public void Open()
    {
        if (_window != null) { _window.MoveToFront(); return; }
        _window = new ApartmentTravelWindow { Title = "Lagoon Coin и подписки" };
        _window.OnClose += Close;
        _window.Panel.OnClose += Close;
        _window.Panel.OnAction += Action;
        Request();
        _window.OpenCentered();
    }
    private void Request()
    {
        if (_window == null || _loading) return;
        _token = Guid.Empty;
        var request = _request = Guid.NewGuid();
        _loading = true;
        _window.Panel.SetState("LagoonEconomy", new TguiData().Bool("loading", true).ToString(), _window.Title!);
        RaiseNetworkEvent(new LagoonEconomyRequest(request));
        Robust.Shared.Timing.Timer.Spawn(12000, () =>
        {
            if (_window == null || _request != request || !_loading) return;
            _loading = false;
            _window.Panel.SetState("LagoonEconomy", new TguiData().String("error", "Сервер не ответил. Попробуйте загрузить данные ещё раз.").ToString(), _window.Title!);
        });
    }
    public void RequestPaidNames() => RaiseNetworkEvent(new LagoonPaidSupportersRequest());
    private void OnState(LagoonEconomyStateEvent ev)
    {
        if (_window == null || ev.Request != _request) return;
        _loading = false;
        _token = ev.Token;
        _window.Panel.SetState("LagoonEconomy", ev.Data, _window.Title!);
    }
    private void Action(string action, string payload)
    {
        if (action == "close") { Close(); return; }
        if (action == "retry") { Request(); return; }
        if (_token == Guid.Empty) return;
        if (action == "focus") { _window?.Panel.Web.FocusTextInput(); return; }
        if (!TguiActionData.TryParse(payload, out var fields, 1024) || !Guid.TryParse(fields!.String("order"), out var order)) return;
        RaiseNetworkEvent(new LagoonEconomyAction(_token, action, fields.String("product") ?? "", order));
    }
    private void Close()
    {
        var window = _window; _window = null;
        _token = _request = Guid.Empty;
        _loading = false;
        if (window == null) return;
        window.Close(); window.Panel.Web.ReleaseTextInput();
        Robust.Shared.Timing.Timer.Spawn(3000, () => {
#pragma warning disable CS0618
            if (!window.Disposed) window.Dispose();
#pragma warning restore CS0618
        });
    }
}

using Content.Client._DeepLagoon.WebUI;
using Content.Client.Power.SMES;
using Content.Client.UserInterface.Controls;
using Content.Shared.Power;
using Robust.Client.UserInterface;

namespace Content.Client.Power.Battery;

public sealed partial class BatteryBoundUserInterface
{
    private BatteryTguiWindow? _tguiWindow;
    private BatteryBuiState? _latestState;
    private bool _classic;
    private float _readyWait;
    private string _title = "";

    private bool OpenTgui()
    {
        if (_classic || !WindowInterfaceStyle.UseTgui("BatteryStorage"))
            return false;

        _readyWait = 0;
        var window = new BatteryTguiWindow();
        window.OnClose += Close;
        WindowInterfaceStyle.OpenPresentation(this, window, _menu);
        _tguiWindow = window;
        window.SetInfoFromEntity(EntMan, Owner);
        _title = window.Title ?? "";
        window.Panel.OnClose += Close;
        window.Panel.OnAction += OnTguiAction;
        window.Panel.BindStyle(window, "BatteryStorage", () => true, SwitchInterface);
        window.Panel.SetNativeFallback(DeferClassic);
        window.Frame += OnTguiFrame;
        window.Panel.SetStaticData(new TguiData().Object("labels", BatteryTguiData.Labels()).ToString());
        PublishTgui();
        return true;
    }

    private void OnTguiFrame(float seconds)
    {
        if (_tguiWindow == null)
            return;
        if (_tguiWindow.Panel.Web.IsReady)
            _readyWait = 0;
        else if ((_readyWait += seconds) >= 8f)
            DeferClassic();
    }

    private void PublishTgui()
    {
        if (_tguiWindow == null)
            return;

        _tguiWindow.Panel.SetIncrementalState("BatteryStorage",
            BatteryTguiData.Build(_latestState, _title, EntMan.HasComponent<SmesComponent>(Owner)), _title);
    }

    private void OnTguiAction(string action, string payload)
    {
        if (_tguiWindow == null || _latestState is not { } state || _pred == null ||
            !TguiActionData.TryParse(payload, out var data) || data == null)
            return;

        switch (action)
        {
            case "input" when bool.TryParse(data.String("on"), out var on):
                _pred.SendMessage(new BatterySetInputBreakerMessage(on));
                state.CanCharge = on;
                break;
            case "output" when bool.TryParse(data.String("on"), out var on):
                _pred.SendMessage(new BatterySetOutputBreakerMessage(on));
                state.CanDischarge = on;
                break;
            case "charge-rate" when data.TryFloat("value", out var value):
                state.MaxChargeRate = Math.Clamp(value, state.MinMaxChargeRate, state.MaxMaxChargeRate);
                _chargeRateCoalescer.Set(state.MaxChargeRate);
                break;
            case "discharge-rate" when data.TryFloat("value", out var value):
                state.MaxSupply = Math.Clamp(value, state.MinMaxSupply, state.MaxMaxSupply);
                _dischargeRateCoalescer.Set(state.MaxSupply);
                break;
            default:
                return;
        }

        PublishTgui();
    }

    private void DeferClassic() => IoCManager.Resolve<IUserInterfaceManager>().DeferAction(() =>
    {
        if (_tguiWindow == null || !IsOpened)
            return;
        _classic = true;
        CloseTgui();
        // Keep the same prediction queue and pending slider inputs during fallback.
        OpenClassic();
    });

    private void CloseTgui()
    {
        if (_tguiWindow is not { } window)
            return;
        _tguiWindow = null;
        window.Frame -= OnTguiFrame;
        window.Panel.OnClose -= Close;
        window.Panel.OnAction -= OnTguiAction;
        WindowInterfaceStyle.ClosePresentation(this, window);
#pragma warning disable CS0618
        if (!window.Disposed)
            window.Dispose();
#pragma warning restore CS0618
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseTgui();
        base.Dispose(disposing);
    }

    private void SwitchInterface(bool tgui)
    {
        if (!IsOpened) return;
        CloseTgui();
        if (_menu is { } native)
            WindowInterfaceStyle.HideForSwitch(this, native);
        _classic = !tgui;

        if (!OpenTgui()) OpenClassic();
    }
}

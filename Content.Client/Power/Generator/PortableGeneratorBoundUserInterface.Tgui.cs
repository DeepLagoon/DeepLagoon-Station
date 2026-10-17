using Content.Client._DeepLagoon.WebUI;
using Content.Client.DoAfter;
using Content.Client.UserInterface.Controls;
using Content.Shared.Power.Generator;
using Robust.Client.UserInterface;

namespace Content.Client.Power.Generator;

public sealed partial class PortableGeneratorBoundUserInterface
{
    private GeneratorTguiWindow? _tguiWindow;
    private PortableGeneratorComponentBuiState? _latestState;
    private bool _classic;
    private float _readyWait;
    private float _refresh;

    private bool OpenTgui()
    {
        if (_classic || !TguiPanel.Enabled)
            return false;
        _readyWait = _refresh = 0;
        var window = this.CreateWindow<GeneratorTguiWindow>();
        _tguiWindow = window;
        window.SetInfoFromEntity(EntMan, Owner);
        window.Panel.OnClose += Close;
        window.Panel.OnAction += OnTguiAction;
        window.Panel.SetNativeFallback(DeferClassic);
        window.Panel.SetStaticData(new TguiData().Object("labels", GeneratorTguiData.Labels()).ToString());
        window.Frame += OnTguiFrame;
        PublishTgui();
        return true;
    }

    private void OnTguiFrame(float seconds)
    {
        if (_tguiWindow == null)
            return;
        if (_tguiWindow.Panel.Web.IsReady)
            _readyWait = 0;
        else if ((_readyWait += seconds) >= 8)
        {
            DeferClassic();
            return;
        }
        // DoAfters and anchoring can change between BUI snapshots.
        if ((_refresh += seconds) < .1f)
            return;
        _refresh = 0;
        PublishTgui();
    }

    private bool TryStartProgress(out float progress)
    {
        if (EntMan.TryGetComponent<ActiveGeneratorRevvingComponent>(Owner, out var revving) &&
            EntMan.TryGetComponent<PortableGeneratorComponent>(Owner, out var portable))
        {
            progress = (float) (revving.CurrentTime / portable.StartTime);
            return true;
        }
        return EntMan.System<DoAfterSystem>().TryFindActiveDoAfter<GeneratorStartedEvent>(Owner, out _, out _, out progress);
    }

    private void PublishTgui()
    {
        if (_tguiWindow == null)
            return;
        var anchored = EntMan.GetComponent<TransformComponent>(Owner).Anchored;
        var starting = TryStartProgress(out var progress) && anchored;
        var title = EntMan.GetComponent<MetaDataComponent>(Owner).EntityName;
        EntMan.TryGetComponent<FuelGeneratorComponent>(Owner, out var fuel);
        EntMan.TryGetComponent<PortableGeneratorComponent>(Owner, out var portable);
        var data = GeneratorTguiData.Build(_latestState, fuel, title, portable?.PanelStyle ?? "steel",
            portable?.PanelFuel ?? "dl-generator-fuel-generic", anchored, starting, progress);
        var switcher = EntMan.System<SharedPowerSwitchableSystem>();
        var canSwitch = EntMan.TryGetComponent<PowerSwitchableComponent>(Owner, out var output);
        data.Bool("canSwitch", canSwitch);
        if (output != null)
        {
            var voltage = switcher.VoltageString(switcher.GetVoltage(Owner, output));
            var next = switcher.VoltageString(switcher.GetNextVoltage(Owner, output));
            data.String("voltage", voltage).String("nextVoltage", next)
                .String("switchText", Loc.GetString("power-switchable-switch-voltage", ("voltage", next)));
        }
        _tguiWindow.Panel.SetIncrementalState("PortableGenerator", data, title);
    }

    private void OnTguiAction(string action, string payload)
    {
        if (_latestState is not { } state || _tguiWindow == null ||
            !TguiActionData.TryParse(payload, out var data) || data == null)
            return;
        switch (action)
        {
            case "power" when data.TryInt("value", out var power):
                if (power >= 0 && power <= state.MaximumPower / 1000)
                    SetTargetPower(power);
                break;
            case "start" when !state.On && EntMan.GetComponent<TransformComponent>(Owner).Anchored:
                Start();
                break;
            case "stop":
                Stop();
                break;
            case "switch" when !state.On && EntMan.HasComponent<PowerSwitchableComponent>(Owner):
                SwitchOutput();
                break;
            case "eject":
                EjectFuel();
                break;
        }
    }

    private void DeferClassic() => IoCManager.Resolve<IUserInterfaceManager>().DeferAction(() =>
    {
        if (_tguiWindow == null || !IsOpened)
            return;
        _classic = true;
        CloseTgui();
        OpenClassic();
    });

    private void CloseTgui()
    {
        if (_tguiWindow is not { } window)
            return;
        _tguiWindow = null;
        window.OnClose -= Close;
        window.Frame -= OnTguiFrame;
        window.Panel.OnClose -= Close;
        window.Panel.OnAction -= OnTguiAction;
        window.Close();
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
}

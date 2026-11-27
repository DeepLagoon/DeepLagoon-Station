using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Controls;
using Content.Shared._DeepLagoon.Mantis;
using Robust.Client.UserInterface;

namespace Content.Client._DeepLagoon.Mantis;

public sealed partial class PsiSpectrometerBoundUserInterface
{
    private PsiSpectrometerTguiWindow? _tguiWindow;
    private bool _classic;
    private float _readyWait;

    private bool OpenTgui()
    {
        if (_classic || !WindowInterfaceStyle.UseTgui("PsiSpectrometer"))
            return false;

        _readyWait = 0;
        var window = new PsiSpectrometerTguiWindow();
        window.OnClose += Close;
        WindowInterfaceStyle.OpenPresentation(this, window, null);
        _tguiWindow = window;
        window.Panel.OnClose += Close;
        window.Panel.OnAction += OnTguiAction;
        window.Panel.BindStyle(window, "PsiSpectrometer", () => true, SwitchInterface);
        window.Panel.SetNativeFallback(DeferClassic);
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
        else if ((_readyWait += seconds) >= 8f)
            DeferClassic();
    }

    private void PublishTgui()
    {
        if (_tguiWindow == null || _latest is not { } state)
            return;

        _tguiWindow.Panel.SetIncrementalState("PsiSpectrometer",
            PsiSpectrometerTguiData.Build(state), Loc.GetString("psi-spectrometer-title"));
    }

    private void OnTguiAction(string action, string payload)
    {
        if (_tguiWindow == null || !TguiActionData.TryParse(payload, out var data) || data == null)
            return;

        switch (action)
        {
            case "scan":
                SendMessage(new PsiSpectrometerScanMessage());
                break;
            case "hunter" when bool.TryParse(data.String("on"), out var on):
                SendMessage(new PsiSpectrometerHunterMessage(on));
                break;
            case "map" when bool.TryParse(data.String("on"), out var map):
                SendMessage(new PsiSpectrometerMapMessage(map));
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

    private void SwitchInterface(bool tgui)
    {
        if (!IsOpened)
            return;

        CloseTgui();
        if (_window != null)
            WindowInterfaceStyle.HideForSwitch(this, _window);

        _classic = !tgui;

        if (!OpenTgui())
            OpenClassic();
    }

    private void CloseTgui()
    {
        if (_tguiWindow is not { } window)
            return;

        _tguiWindow = null;
        window.Frame -= OnTguiFrame;
        window.Panel.OnClose -= Close;
        window.Panel.OnAction -= OnTguiAction;
        WindowInterfaceStyle.ClosePresentation(this, window);
    }
}

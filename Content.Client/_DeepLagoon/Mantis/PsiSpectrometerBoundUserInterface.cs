using Content.Shared._DeepLagoon.Mantis;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._DeepLagoon.Mantis;

[UsedImplicitly]
public sealed partial class PsiSpectrometerBoundUserInterface(EntityUid owner, Enum uiKey)
    : BoundUserInterface(owner, uiKey)
{
    private PsiSpectrometerWindow? _window;
    private PsiSpectrometerState? _latest;

    protected override void Open()
    {
        base.Open();

        if (OpenTgui())
            return;

        OpenClassic();
    }

    private void OpenClassic()
    {
        _window = this.CreateWindow<PsiSpectrometerWindow>();
        _window.OnScan += () => SendMessage(new PsiSpectrometerScanMessage());
        _window.OnHunter += enabled => SendMessage(new PsiSpectrometerHunterMessage(enabled));
        _window.OnMapOverlay += enabled => SendMessage(new PsiSpectrometerMapMessage(enabled));

        if (_latest is { } state)
            _window.UpdateState(state);
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is not PsiSpectrometerState spectrometer)
            return;

        _latest = spectrometer;
        _window?.UpdateState(spectrometer);
        PublishTgui();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseTgui();

        base.Dispose(disposing);
    }
}

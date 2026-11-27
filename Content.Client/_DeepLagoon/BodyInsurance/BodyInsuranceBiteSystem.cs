using Content.Shared._DeepLagoon.BodyInsurance;
using Content.Shared.GameTicking;
using Robust.Client.Graphics;

namespace Content.Client._DeepLagoon.BodyInsurance;

public sealed class BodyInsuranceBiteSystem : EntitySystem
{
    [Dependency]
    private IOverlayManager _overlayManager = default!;

    private BodyInsuranceBiteOverlay _overlay = default!;

    public override void Initialize()
    {
        base.Initialize();

        _overlay = new BodyInsuranceBiteOverlay();

        _overlayManager.AddOverlay(_overlay);

        SubscribeNetworkEvent<BodyInsuranceBiteEvent>(
            OnBite);

        SubscribeNetworkEvent<RoundRestartCleanupEvent>(
            OnRoundRestart);
    }

    private void OnBite(BodyInsuranceBiteEvent args)
    {
        _overlay.Stop();
        _overlay.Play();
    }

    private void OnRoundRestart(
        RoundRestartCleanupEvent args)
    {
        _overlay.Stop();
    }

    public override void Shutdown()
    {
        _overlayManager.RemoveOverlay(_overlay);

        base.Shutdown();
    }
}

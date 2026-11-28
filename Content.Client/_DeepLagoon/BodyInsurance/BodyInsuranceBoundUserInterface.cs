using Content.Shared._DeepLagoon.BodyInsurance;

namespace Content.Client._DeepLagoon.BodyInsurance;

public sealed class BodyInsuranceBoundUserInterface
    : BoundUserInterface
{
    private BodyInsuranceWindow? _window;

    public BodyInsuranceBoundUserInterface(
        EntityUid owner,
        Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = new BodyInsuranceWindow();

        _window.OnClose += Close;

        _window.PurchaseRequested += OnPurchase;

        _window.OpenCentered();
    }

    private void OnPurchase()
    {
        SendMessage(new BodyInsurancePurchaseMessage());
    }

    protected override void UpdateState(
        BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is not BodyInsuranceUiState insuranceState)
            return;

        _window?.UpdateState(insuranceState);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
            return;

        _window?.Dispose();
    }
}

using Content.Server._NF.Bank;
using Content.Server.Popups;
using Content.Shared._DeepLagoon.BodyInsurance;
using Content.Shared.Mobs.Systems;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.BodyInsurance;

public sealed partial class BodyInsuranceMachineSystem : EntitySystem
{
    [Dependency] private BodyInsuranceSystem _insurance = default!;
    [Dependency] private BankSystem _bank = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private PopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<
            BodyInsuranceMachineComponent,
            BoundUIOpenedEvent>(OnUiOpened);

        SubscribeLocalEvent<
            BodyInsuranceMachineComponent,
            BodyInsurancePurchaseMessage>(OnPurchase);
    }

    private void OnUiOpened(
        EntityUid uid,
        BodyInsuranceMachineComponent component,
        BoundUIOpenedEvent args)
    {
        if (args.Actor is not { Valid: true } user)
            return;

        UpdateUi(uid, component, user);
    }

    private async void OnPurchase(
        EntityUid uid,
        BodyInsuranceMachineComponent component,
        BodyInsurancePurchaseMessage args)
    {
        if (args.Actor is not { Valid: true } user)
            return;

        if (!TryComp<ActivatableUIComponent>(uid, out var activatable))
            return;

        if (activatable.CurrentSingleUser != user)
            return;

        if (!_ui.IsUiOpen(uid, BodyInsuranceUiKey.Key, user))
            return;

        if (!_mobState.IsAlive(user))
        {
            ShowPopup(user, "body-insurance-not-alive");
            return;
        }

        if (_insurance.IsInsured(user))
        {
            ShowPopup(user, "body-insurance-already-insured");
            UpdateUi(uid, component, user);
            return;
        }

        var price = _insurance.GetPrice(user, component);

        if (!_bank.TryGetBalance(user, out var balance)
            || balance < price)
        {
            ShowPopup(user, "body-insurance-no-money");
            UpdateUi(uid, component, user);
            return;
        }

        var purchased = await _insurance.TryPurchaseAsync(
            user, component);

        // Игрок или автомат могли быть удалены
        // во время обращения к базе данных.
        if (!Exists(user) || !Exists(uid))
            return;

        if (!purchased)
        {
            ShowPopup(user, "body-insurance-purchase-failed");

            if (_ui.IsUiOpen(uid, BodyInsuranceUiKey.Key, user))
                UpdateUi(uid, component, user);

            return;
        }

        ShowPopup(user, "body-insurance-purchased");

        if (TryComp<ActorComponent>(user, out var actor))
        {
            RaiseNetworkEvent(
                new BodyInsuranceBiteEvent(),
                actor.PlayerSession);
        }

        if (_ui.IsUiOpen(uid, BodyInsuranceUiKey.Key, user))
            _ui.CloseUi(uid, BodyInsuranceUiKey.Key, user);
    }

    private void UpdateUi(
        EntityUid uid,
        BodyInsuranceMachineComponent component,
        EntityUid user)
    {
        var price = _insurance.GetPrice(user, component);
        var insured = _insurance.IsInsured(user);

        var subscriptionPrice = component.SubscriptionCost;
        var hasSubscription = _insurance.HasSubscription(user);
        var purchaseCount = _insurance.GetPurchaseCount(user);

        var canPurchase =
            !insured &&
            _mobState.IsAlive(user) &&
            _bank.TryGetBalance(user, out var balance) &&
            balance >= price;

        var state = new BodyInsuranceUiState(
            price,
            canPurchase,
            insured,
            subscriptionPrice,
            hasSubscription,
            purchaseCount);

        _ui.SetUiState(
            uid,
            BodyInsuranceUiKey.Key,
            state);
    }

    private void ShowPopup(EntityUid user, string message)
    {
        _popup.PopupEntity(
            Loc.GetString(message),
            user,
            user);
    }
}

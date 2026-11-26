using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.BodyInsurance;

public sealed partial class BodyInsuranceSystem
{
    private const long DiscoveryReward = 10000;

    private bool CanReceiveRescueReward(
        EntityUid body,
        EntityUid rescuer,
        BodyInsuranceComponent insurance)
    {
        // Нельзя получить награду за самого себя.
        if (body == rescuer)
            return false;

        if (TerminatingOrDeleted(body) ||
            TerminatingOrDeleted(rescuer))
            return false;

        if (!TryComp<ActorComponent>(
                rescuer, out var actor))
            return false;

        // Нельзя получить награду за тело
        // со своего же игрового аккаунта.
        if (insurance.InsuranceAccount is { } owner &&
            actor.PlayerSession.UserId == owner)
            return false;

        return true;
    }

    public void RewardBodyDiscovery(
        EntityUid body,
        EntityUid rescuer)
    {
        if (!TryComp<BodyInsuranceComponent>(
                body, out var insurance))
            return;

        if (insurance.RescueDiscoveryPaid)
            return;

        if (!_mobState.IsDead(body))
            return;

        if (insurance.RescueRevivalReward <= 0)
            return;

        if (!CanReceiveRescueReward(
                body, rescuer, insurance))
            return;

        // Блокируем повторную выплату.
        insurance.RescueDiscoveryPaid = true;

        if (!_bank.TryBankDeposit(
                rescuer, DiscoveryReward))
        {
            insurance.RescueDiscoveryPaid = false;

            Log.Warning(
                $"BodyInsurance: Discovery payment failed for {rescuer}.");

            return;
        }

        _popup.PopupEntity(
            Loc.GetString(
                "body-insurance-discovery-reward",
                ("amount", DiscoveryReward)),
            rescuer,
            rescuer);

        Log.Info(
            $"BodyInsurance: {rescuer} received " +
            $"{DiscoveryReward} credits for finding {body}.");
    }

    public void RewardBodyRevival(
        EntityUid body,
        EntityUid medic)
    {
        if (!TryComp<BodyInsuranceComponent>(
                body, out var insurance))
            return;

        if (insurance.RescueRevivalReward <= 0)
            return;

        // Тело действительно должно быть реанимировано.
        if (_mobState.IsDead(body))
            return;

        if (!CanReceiveRescueReward(
                body, medic, insurance))
            return;

        var reward = insurance.RescueRevivalReward;

        // Награда одноразовая.
        insurance.RescueRevivalReward = 0;

        if (!_bank.TryBankDeposit(medic, reward))
        {
            insurance.RescueRevivalReward = reward;

            Log.Warning(
                $"BodyInsurance: Revival payment failed for {medic}.");

            return;
        }

        _popup.PopupEntity(
            Loc.GetString(
                "body-insurance-revival-reward",
                ("amount", reward)),
            medic,
            medic);

        Log.Info(
            $"BodyInsurance: {medic} received " +
            $"{reward} credits for reviving {body}.");
    }
}

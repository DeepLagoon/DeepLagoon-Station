using Content.Server.Implants;
using Content.Server.Body.Components;
using Content.Shared.Implants;
using Content.Shared.Implants.Components;
using Content.Shared._DeepLagoon.BodyInsurance;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.BodyInsurance;

public sealed partial class BodyInsuranceSystem
{
    [Dependency]
    private SubdermalImplantSystem _implants = default!;

    // Не допускаем повторного укуса при покупке через автомат.
    private readonly HashSet<EntityUid> _installingInsurance = new();

    private void InitializeInsuranceImplants()
    {
        SubscribeLocalEvent<
            BodyInsuranceComponent,
            EntRemovedFromContainerMessage>(
                OnInsuranceImplantRemoved);

        SubscribeLocalEvent<
            BodyInsuranceImplantComponent,
            ImplantImplantedEvent>(
                OnInsuranceImplanted);

        SubscribeLocalEvent<
            BodyInsuranceComponent,
            AddImplantAttemptEvent>(
                OnInsuranceImplantAttempt);
    }

    private EntityUid? InstallInsuranceImplant(EntityUid user)
    {
        if (!_installingInsurance.Add(user))
            return null;

        try
        {
            var implant = _implants.AddImplant(
                user, "BodyInsuranceImplant");

            if (implant is not { } implantUid)
                return null;

            if (!HasComp<BodyInsuranceImplantComponent>(implantUid)
                || !_containers.TryGetContainer(
                    user,
                    ImplanterComponent.ImplantSlotId,
                    out var container)
                || !container.Contains(implantUid))
            {
                QueueDel(implantUid);
                return null;
            }

            return implantUid;
        }
        finally
        {
            _installingInsurance.Remove(user);
        }
    }

    // Запрещаем устанавливать второй страховой имплант.
    private void OnInsuranceImplantAttempt(
        EntityUid uid,
        BodyInsuranceComponent insurance,
        AddImplantAttemptEvent args)
    {
        if (!insurance.Insured)
            return;

        if (!HasComp<BodyInsuranceImplantComponent>(args.Implant))
            return;

        args.Cancel();

        _popup.PopupEntity(
            "У персонажа уже есть действующая страховка.",
            uid,
            args.User);
    }

    // Срабатывает при установке импланта.
    private void OnInsuranceImplanted(
        EntityUid uid,
        BodyInsuranceImplantComponent implant,
        ref ImplantImplantedEvent args)
    {
        if (args.Implanted is not { } user)
            return;

        if (TerminatingOrDeleted(user))
            return;

        // Покупка через автомат обрабатывается отдельно.
        if (_installingInsurance.Contains(user))
            return;

        if (!_containers.TryGetContainer(
                user,
                ImplanterComponent.ImplantSlotId,
                out var container)
            || !container.Contains(uid))
            return;

        if (IsInsured(user))
            return;

        var synthetic = IsSynthetic(user);

        FixedPoint2 bloodCost = FixedPoint2.Zero;
        FixedPoint2 damageCost = FixedPoint2.Zero;

        if (synthetic)
        {
            if (!_thresholds.TryGetDeadThreshold(
                    user, out var threshold))
                return;

            damageCost = FixedPoint2.New(
                (float)threshold.Value *
                implant.SyntheticDamageFraction);
        }
        else
        {
            if (!TryComp<BloodstreamComponent>(
                    user, out var bloodstream))
                return;

            var availableBlood =
                _bloodstream.GetBloodLevelPercentage(user);

            bloodCost = FixedPoint2.New(
                (float)bloodstream.BloodMaxVolume *
                Math.Min(availableBlood, implant.BloodCostFraction));
        }

        var insurance =
            EnsureComp<BodyInsuranceComponent>(user);

        if (!_actions.AddAction(
                user,
                ref insurance.ReturnAction,
                "ActionBodyInsuranceReturn"))
        {
            Log.Error(
                $"BodyInsurance: Failed to grant action to {user}.");

            return;
        }

        insurance.InsuranceImplant = uid;
        insurance.Insured = true;

        insurance.PaidInsurancePrice = implant.PaidPrice;
        insurance.RescueDiscoveryPaid = false;
        insurance.RescueRevivalReward = 0;

        insurance.InsuranceAccount =
            TryComp<ActorComponent>(user, out var owner)
                ? owner.PlayerSession.UserId
                : null;

        insurance.ReturnDelay = implant.ReturnDelay;
        insurance.ReturnDuration = implant.ReturnDuration;
        insurance.PlayerBlockingRadius = implant.PlayerBlockingRadius;
        insurance.MinReturnDistance = implant.MinReturnDistance;
        insurance.MaxReturnDistance = implant.MaxReturnDistance;
        insurance.ExpeditionRescueDamage = implant.ExpeditionRescueDamage;

        insurance.ReturnBluntDamage = implant.ReturnBluntDamage;
        insurance.ReturnPiercingDamage = implant.ReturnPiercingDamage;
        insurance.ReturnSlashDamage = implant.ReturnSlashDamage;

        // Если имплант установлен в мёртвое тело,
        // начинаем отсчёт возвращения заново.
        if (_mobState.IsDead(user))
        {
            insurance.DeathTime = _timing.CurTime;

            _actions.SetEnabled(
                insurance.ReturnAction, true);

            _actions.SetCooldown(
                insurance.ReturnAction.Value,
                _timing.CurTime,
                _timing.CurTime +
                TimeSpan.FromSeconds(insurance.ReturnDelay));
        }
        else
        {
            insurance.DeathTime = null;

            _actions.SetEnabled(
                insurance.ReturnAction, false);
        }

        // Физическая плата за повторную установку.
        if (synthetic)
        {
            var damage = new DamageSpecifier();

            damage.DamageDict["Blunt"] = damageCost;

            _damage.TryChangeDamage(
                user,
                damage,
                ignoreResistances: true,
                canSever: false,
                originFlag:
                    DamageableSystem.DamageOriginFlag.BodyInsurance);
        }
        else
        {
            if (bloodCost > FixedPoint2.Zero)
            {
                _bloodstream.TryModifyBloodLevel(
                    user, -bloodCost);
            }

            _bloodstream.TryModifyBleedAmount(
                user, implant.BiteBleeding);
        }

        // Укус, звук и анимация нового владельца.
        if (TryComp<ActorComponent>(user, out var actor))
        {
            RaiseNetworkEvent(
                new BodyInsuranceBiteEvent(),
                actor.PlayerSession);
        }

        _popup.PopupEntity(
            "Страховой имплант активирован.",
            user,
            user);

        Log.Info(
            $"BodyInsurance: Implant {uid} activated on {user}.");
    }

    private void OnInsuranceImplantRemoved(
        EntityUid uid,
        BodyInsuranceComponent insurance,
        EntRemovedFromContainerMessage args)
    {
        if (TerminatingOrDeleted(uid))
            return;

        if (args.Container.ID != ImplanterComponent.ImplantSlotId)
            return;

        if (insurance.InsuranceImplant != args.Entity)
            return;

        // Сохраняем извлечённый имплант.
        insurance.InsuranceImplant = null;

        if (!insurance.Insured)
            return;

        ConsumeInsurance(uid);

        _popup.PopupEntity(
            "Страховой имплант извлечён. Страховка аннулирована.",
            uid,
            uid);

        Log.Info(
            $"BodyInsurance: Implant removed from {uid}.");
    }

    private bool HasInsuranceImplant(
        EntityUid user,
        BodyInsuranceComponent insurance)
    {
        if (insurance.InsuranceImplant is not { } implant)
            return false;

        if (TerminatingOrDeleted(implant))
            return false;

        return _containers.TryGetContainer(
                   user,
                   ImplanterComponent.ImplantSlotId,
                   out var container)
               && container.Contains(implant);
    }

    private void SaveInsuranceImplantTerms(
        EntityUid uid,
        BodyInsuranceMachineComponent machine,
        long price)
    {
        if (!TryComp<BodyInsuranceImplantComponent>(
                uid, out var implant))
            return;

        implant.PaidPrice = price;

        implant.BloodCostFraction = machine.BloodCostFraction;
        implant.SyntheticDamageFraction = machine.SyntheticDamageFraction;
        implant.BiteBleeding = machine.BiteBleeding;

        implant.ReturnDelay = machine.ReturnDelay;
        implant.ReturnDuration = machine.ReturnDuration;
        implant.PlayerBlockingRadius = machine.PlayerBlockingRadius;
        implant.MinReturnDistance = machine.MinReturnDistance;
        implant.MaxReturnDistance = machine.MaxReturnDistance;
        implant.ExpeditionRescueDamage = machine.ExpeditionRescueDamage;

        implant.ReturnBluntDamage = machine.ReturnBluntDamage;
        implant.ReturnPiercingDamage = machine.ReturnPiercingDamage;
        implant.ReturnSlashDamage = machine.ReturnSlashDamage;
    }
}

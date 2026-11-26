using Content.Server._Mono.Cleanup;
using Content.Server._NF.Bank;
using Content.Server.Body.Components;
using Content.Server.Body.Systems;
using Content.Server.Medical.SuitSensors;
using Content.Server.Popups;
using Content.Shared._DeepLagoon.BodyInsurance;
using Content.Shared.Actions;
using Content.Shared.Damage;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.Ghost;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Content.Shared.Medical.SuitSensor;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using System.Numerics;
using Content.Shared.GameTicking;
using Content.Server._DeepLagoon.Currency;
using Content.Server.Database;
using System.Threading.Tasks;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank.Components;

namespace Content.Server._DeepLagoon.BodyInsurance;

public sealed partial class BodyInsuranceSystem : EntitySystem
{

    [Dependency]
    private LagoonCoinSystem _lagoonCoins = default!;

    private readonly HashSet<NetUserId> _pendingPurchases = new();

    private Guid _subscriptionRound = Guid.NewGuid();

    private static readonly TimeSpan ProximityCheckInterval = TimeSpan.FromSeconds(0.5);
    private TimeSpan _nextProximityCheck;

    [Dependency]
    private CleanupHelperSystem _cleanup = default!;

    [Dependency]
    private SharedEntityStorageSystem _entityStorage = default!;

    [Dependency]
    private SuitSensorSystem _suitSensor = default!;

    [Dependency]
    private InventorySystem _inventory = default!;

    [Dependency]
    private SharedContainerSystem _containers = default!;

    [Dependency]
    private SharedDoAfterSystem _doAfter = default!;

    [Dependency]
    private EntityLookupSystem _lookup = default!;

    [Dependency]
    private SharedTransformSystem _transform = default!;

    [Dependency]
    private IRobustRandom _random = default!;

    [Dependency]
    private SharedActionsSystem _actions = default!;

    [Dependency]
    private PopupSystem _popup = default!;
    [Dependency] private BankSystem _bank = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private IGameTiming _timing = default!;

    [Dependency]
    private BloodstreamSystem _bloodstream = default!;

    [Dependency]
    private DamageableSystem _damage = default!;

    [Dependency]
    private MobThresholdSystem _thresholds = default!;

    public override void Initialize()
    {
        base.Initialize();

        InitializeInsuranceImplants();

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);

        SubscribeLocalEvent<
            BodyInsuranceComponent,
            MobStateChangedEvent>(OnMobStateChanged);

        SubscribeLocalEvent<
            BodyInsuranceComponent,
            BodyInsuranceReturnEvent>(OnReturnAction);

        SubscribeLocalEvent<
            BodyInsuranceComponent,
            BodyInsuranceReturnDoAfterEvent>(OnReturnCompleted);

        SubscribeLocalEvent<
            BodyInsuranceComponent,
            DoAfterAttemptEvent<BodyInsuranceReturnDoAfterEvent>>(OnReturnAttempt);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        if (now < _nextProximityCheck)
            return;

        _nextProximityCheck = now + ProximityCheckInterval;

        // Проверяем только мёртвых застрахованных персонажей.
        // Обычные игроки, NPC и живые застрахованные не требуют поиска поблизости.
        var query = EntityQueryEnumerator<BodyInsuranceComponent, TransformComponent>();
        while (query.MoveNext(out var body, out var insurance, out var xform))
        {
            if (!insurance.Insured || insurance.DeathTime == null ||
                !_mobState.IsDead(body) || xform.MapID == MapId.Nullspace ||
                TerminatingOrDeleted(body))
                continue;

            if (HasPlayersNearby(body, insurance.PlayerBlockingRadius))
                ResetReturnCooldown(insurance);
        }
    }

    private void ResetReturnCooldown(BodyInsuranceComponent insurance)
    {
        var now = _timing.CurTime;

        if (insurance.DeathTime == now)
            return;

        insurance.DeathTime = now;

        if (insurance.ReturnAction is { } action)
        {
            _actions.SetCooldown(
                action,
                now,
                now + TimeSpan.FromSeconds(insurance.ReturnDelay));
        }
    }

    public void RemoveEvacuationEquipment(EntityUid body)
    {
        foreach (var slot in LostEquipmentSlots)
        {
            if (!_inventory.TryGetSlotEntity(
                    body, slot, out var item))
                continue;

            if (item is not { } itemUid)
                continue;

            QueueDel(itemUid);
        }
    }

    private static readonly string[] LostEquipmentSlots =
{
    "back",
    "belt",
    "pocket1",
    "pocket2",
    "suitstorage"
};

    private void EnableRescueSensors(EntityUid body)
    {
        if (!_inventory.TryGetSlotEntity(
                body, "jumpsuit", out var jumpsuit))
            return;

        if (!TryComp<SuitSensorComponent>(
                jumpsuit, out var sensor))
            return;

        _suitSensor.SetSensor(
            (jumpsuit.Value, sensor),
            SuitSensorMode.SensorCords);
    }

    private bool TryEvacuateIntoBag(
    EntityUid body,
    EntityCoordinates destination)
    {

        if (!TryComp<BodyInsuranceComponent>(
        body, out var activeInsurance))
            return false;

        if (!activeInsurance.Insured ||
            !HasInsuranceImplant(body, activeInsurance))
            return false;

        // Создаём мешок непосредственно
        // в точке назначения в космосе.
        var bag = Spawn(
            "BodyInsuranceRescueBag",
            destination);

        // Мешок должен быть закрыт для Insert().
        if (_entityStorage.IsOpen(bag))
            _entityStorage.CloseStorage(bag);

        if (!_entityStorage.CanInsert(body, bag))
        {
            QueueDel(bag);

            Log.Warning(
                "BodyInsurance: Cannot fit body into rescue bag.");

            return false;
        }

        // Переносим существующее тело внутрь мешка.
        if (!_entityStorage.Insert(body, bag))
        {
            QueueDel(bag);

            Log.Warning(
                "BodyInsurance: Failed to insert body.");

            return false;
        }

        // Проверяем, что тело действительно внутри.
        if (Transform(body).ParentUid != bag)
        {
            QueueDel(bag);

            Log.Warning(
                "BodyInsurance: Body was not inserted into bag.");

            return false;
        }

        // Дополнительно защищаем само тело.
        EnsureComp<CleanupImmuneComponent>(body);

        EnableRescueSensors(body);

        // Привязываем эвакуационный мешок к спасённому телу.
        var rescueBag =
            EnsureComp<BodyInsuranceRescueBagComponent>(bag);

        rescueBag.Body = body;

        // Активируем награды только после успешной эвакуации.
        if (TryComp<BodyInsuranceComponent>(
                body, out var insurance))
        {
            insurance.RescueDiscoveryPaid = false;

            insurance.RescueRevivalReward =
                insurance.PaidInsurancePrice * 15 / 100;
        }

        Log.Info(
            $"BodyInsurance: Body {body} evacuated into bag {bag}.");

        return true;
    }

    private bool HasPlayersNearby(
    EntityUid body,
    float radius)
    {
        var nearby = _lookup
            .GetEntitiesInRange<ActorComponent>(
                Transform(body).Coordinates, radius);

        foreach (var entity in nearby)
        {
            if (entity.Owner == body)
                continue;

            if (HasComp<GhostComponent>(entity.Owner))
                continue;

            return true;
        }

        return false;
    }

    private bool TryGetReturnPosition(
    BodyInsuranceComponent insurance,
    out EntityCoordinates destination)
    {
        destination = default;

        var query = EntityQueryEnumerator<
            BodyInsuranceMachineComponent,
            TransformComponent>();

        while (query.MoveNext(
                   out var uid, out _, out var xform))
        {
            if (xform.MapUid is not { } mapUid)
                continue;

            if (xform.MapID == MapId.Nullspace)
                continue;

            var origin = _transform.GetMapCoordinates(uid);

            for (var attempt = 0; attempt < 50; attempt++)
            {
                var x = _random.Next(
                    insurance.MinReturnDistance,
                    insurance.MaxReturnDistance + 1);

                var y = _random.Next(
                    insurance.MinReturnDistance,
                    insurance.MaxReturnDistance + 1);

                if (_random.Next(0, 2) == 0)
                    x = -x;

                if (_random.Next(0, 2) == 0)
                    y = -y;

                var position = origin.Position +
                               new Vector2(x, y);

                var candidate = new EntityCoordinates(
                    mapUid, position);

                // Рядом не должно быть станции,
                // шаттла или другого грида.
                if (_cleanup.HasNearbyGrids(candidate, 10f))
                    continue;

                destination = candidate;

                Log.Info(
                    $"BodyInsurance: Destination found: {candidate}");

                return true;
            }
        }

        Log.Warning(
            "BodyInsurance: No valid space destination found.");

        return false;
    }

    private bool IsSynthetic(EntityUid user)
    {
        if (!TryComp<HumanoidAppearanceComponent>(
            user, out var appearance))
            return false;

        return appearance.Species.Id is "Synth" or "IPC";
    }

    private void OnReturnAttempt(
    EntityUid uid,
    BodyInsuranceComponent component,
    DoAfterAttemptEvent<BodyInsuranceReturnDoAfterEvent> args)
    {
        if (!component.Insured ||
            !_mobState.IsDead(uid))
        {
            args.Cancel();
            return;
        }

        // Если фоновая проверка уже сбросила КД, отменяем активацию,
        // даже если игрок успел отойти до следующей проверки DoAfter.
        if (!HasReturnDelayPassed(uid, component.ReturnDelay))
        {
            args.Cancel();
            return;
        }

        // Проверяем окружающих игроков
        // не чаще двух раз в секунду.
        if (_timing.CurTime < component.NextReturnCheck)
            return;

        component.NextReturnCheck =
            _timing.CurTime + TimeSpan.FromSeconds(0.5);

        if (HasPlayersNearby(
                uid, component.PlayerBlockingRadius))
        {
            ResetReturnCooldown(component);
            args.Cancel();
        }
    }

    private void OnReturnCompleted(
    EntityUid uid,
    BodyInsuranceComponent component,
    BodyInsuranceReturnDoAfterEvent args)
    {
        if (args.Cancelled)
        {
            _popup.PopupEntity(
                Loc.GetString(
                    "body-insurance-return-cancelled"),
                uid,
                uid);

            return;
        }

        if (args.Handled)
            return;

        if (args.User != uid)
            return;

        if (!component.Insured)
            return;

        if (!_mobState.IsDead(uid))
            return;

        if (HasPlayersNearby(
                uid, component.PlayerBlockingRadius))
        {
            ResetReturnCooldown(component);
            _popup.PopupEntity(
                Loc.GetString(
                    "body-insurance-return-players-nearby"),
                uid,
                uid);

            return;
        }

        if (!HasReturnDelayPassed(uid, component.ReturnDelay))
            return;

        if (!TryGetReturnPosition(
                component, out var destination))
        {
            _popup.PopupEntity(
                Loc.GetString(
                    "body-insurance-return-failed"),
                uid,
                uid);

            return;
        }

        if (!TryEvacuateIntoBag(uid, destination))
        {
            _popup.PopupEntity(
                Loc.GetString(
                    "body-insurance-return-failed"),
                uid,
                uid);

            return;
        }

        args.Handled = true;

        // Удаляем снаряжение после успешного переноса.
        RemoveEvacuationEquipment(uid);

        // Расходуем страховку.
        ConsumeInsurance(uid);

        // Урон, анимация и звук.
        ApplyReturnBite(uid, component);

        _popup.PopupEntity(
            Loc.GetString(
                "body-insurance-return-success"),
            uid,
            uid);
    }

    private void OnReturnAction(
    EntityUid uid,
    BodyInsuranceComponent component,
    BodyInsuranceReturnEvent args)
    {
        if (args.Handled)
            return;

        if (args.Performer != uid)
            return;

        if (!component.Insured)
            return;

        if (!_mobState.IsDead(uid))
            return;

        if (HasPlayersNearby(
                uid, component.PlayerBlockingRadius))
        {
            ResetReturnCooldown(component);
            _popup.PopupEntity(
                Loc.GetString(
                    "body-insurance-return-players-nearby"),
                uid,
                uid);

            return;
        }

        if (!HasReturnDelayPassed(
                uid, component.ReturnDelay))
        {
            _popup.PopupEntity(
                Loc.GetString(
                    "body-insurance-return-too-early"),
                uid,
                uid);

            return;
        }

        var doAfter = new DoAfterArgs(
            EntityManager,
            uid,
            component.ReturnDuration,
            new BodyInsuranceReturnDoAfterEvent(),
            uid,
            target: uid)
        {
            NeedHand = false,
            RequireCanInteract = false,
            BreakOnMove = false,
            BreakOnDamage = false,

            Hidden = false,
            MultiplyDelay = false,

            BlockDuplicate = true,
            CancelDuplicate = true,

            AttemptFrequency = AttemptFrequency.EveryTick
        };

        if (!_doAfter.TryStartDoAfter(doAfter))
            return;

        args.Handled = true;

        _popup.PopupEntity(
            Loc.GetString(
                "body-insurance-return-started"),
            uid,
            uid);
    }

    private void ApplyReturnBite(
    EntityUid uid,
    BodyInsuranceComponent component)
    {
        var damage = new DamageSpecifier();

        damage.DamageDict["Blunt"] =
            FixedPoint2.New(component.ReturnBluntDamage);

        damage.DamageDict["Piercing"] =
            FixedPoint2.New(component.ReturnPiercingDamage);

        damage.DamageDict["Slash"] =
            FixedPoint2.New(component.ReturnSlashDamage);

        _damage.TryChangeDamage(
            uid,
            damage,
            ignoreResistances: true,
            interruptsDoAfters: false,
            canSever: false,
            originFlag:
                DamageableSystem.DamageOriginFlag.BodyInsurance);

        // Анимация только если игрок находится в теле.
        if (TryComp<ActorComponent>(uid, out var actor))
        {
            RaiseNetworkEvent(
                new BodyInsuranceBiteEvent(),
                actor.PlayerSession);
        }
    }

    public int GetDeathCount(EntityUid user)
    {
        return TryComp<BodyInsuranceComponent>(
            user, out var insurance)
            ? insurance.DeathCount
            : 0;
    }
    public long GetPrice(
    EntityUid user,
    BodyInsuranceMachineComponent machine)
    {
        var purchases = 0;

        if (TryComp<BodyInsuranceComponent>(user, out var insurance))
            purchases = insurance.PurchaseCount;

        var price = (decimal)machine.BasePrice;
        var multiplier = (decimal)machine.PurchasePriceMultiplier;

        for (var i = 0; i < purchases; i++)
        {
            price *= multiplier;

            if (price >= int.MaxValue)
                return int.MaxValue;
        }

        return (long)Math.Ceiling(price);
    }

    public int GetPurchaseCount(EntityUid user)
    {
        return TryComp<BodyInsuranceComponent>(user, out var insurance)
            ? insurance.PurchaseCount
            : 0;
    }

    public bool IsInsured(EntityUid user)
    {
        return TryComp<BodyInsuranceComponent>(user, out var insurance)
               && insurance.Insured;
    }
    private void OnMobStateChanged(
    EntityUid uid,
    BodyInsuranceComponent component,
    MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Dead)
        {
            RegisterDeath(uid);
            return;
        }

        if (args.OldMobState == MobState.Dead)
        {
            RegisterRevival(uid);
        }
    }

    private bool TryPurchaseInternal(
        EntityUid user,
        BodyInsuranceMachineComponent machine)
    {
        if (!_mobState.IsAlive(user))
            return false;

        if (IsInsured(user))
            return false;

        var synthetic = IsSynthetic(user);

        FixedPoint2 bloodCost = FixedPoint2.Zero;
        FixedPoint2 damageCost = FixedPoint2.Zero;

        if (synthetic)
        {
            if (!_thresholds.TryGetDeadThreshold(
                    user, out var threshold))
                return false;

            damageCost = FixedPoint2.New(
                (float)threshold.Value *
                machine.SyntheticDamageFraction);
        }
        else
        {
            if (!TryComp<BloodstreamComponent>(
                    user, out var bloodstream))
                return false;

            var bloodFraction =
                _bloodstream.GetBloodLevelPercentage(user);

            if (bloodFraction < machine.BloodCostFraction)
                return false;

            bloodCost = FixedPoint2.New(
                (float)bloodstream.BloodMaxVolume *
                machine.BloodCostFraction);
        }

        var price = GetPrice(user, machine);

        if (!_bank.TryGetBalance(user, out var balance))
            return false;

        if (balance < price)
            return false;

        var implant = InstallInsuranceImplant(user);

        if (implant is not { } installedImplant)
            return false;

        if (!_bank.TryBankWithdraw(user, price))
        {
            QueueDel(installedImplant);
            return false;
        }


        if (synthetic)
        {
            var damage = new DamageSpecifier();

            damage.DamageDict["Blunt"] = damageCost;

            if (_damage.TryChangeDamage(
                    user,
                    damage,
                    ignoreResistances: true) == null)
            {
                QueueDel(installedImplant);
                _bank.TryBankDeposit(user, price);
                return false;
            }
        }
        else
        {
            if (!_bloodstream.TryModifyBloodLevel(
                    user, -bloodCost))
            {
                QueueDel(installedImplant);
                _bank.TryBankDeposit(user, price);
                return false;
            }

            _bloodstream.TryModifyBleedAmount(
                user, machine.BiteBleeding);
        }

        var insurance =
            EnsureComp<BodyInsuranceComponent>(user);

        insurance.InsuranceImplant = installedImplant;
        insurance.Insured = true;
        insurance.DeathTime = null;
        insurance.PaidInsurancePrice = price;
        insurance.RescueDiscoveryPaid = false;
        insurance.RescueRevivalReward = 0;

        insurance.InsuranceAccount =
            TryComp<ActorComponent>(user, out var ownerActor)
                ? ownerActor.PlayerSession.UserId
                : null;

        insurance.ReturnDelay = machine.ReturnDelay;
        insurance.ReturnDuration = machine.ReturnDuration;
        insurance.PlayerBlockingRadius = machine.PlayerBlockingRadius;
        insurance.MinReturnDistance = machine.MinReturnDistance;
        insurance.MaxReturnDistance = machine.MaxReturnDistance;
        insurance.ExpeditionRescueDamage = machine.ExpeditionRescueDamage;
        insurance.ReturnBluntDamage = machine.ReturnBluntDamage;
        insurance.ReturnPiercingDamage = machine.ReturnPiercingDamage;
        insurance.ReturnSlashDamage = machine.ReturnSlashDamage;

        _actions.AddAction(
            user,
            ref insurance.ReturnAction,
            "ActionBodyInsuranceReturn");

        _actions.SetEnabled(
            insurance.ReturnAction,
            false);

        if (_mobState.IsDead(user))
            RegisterDeath(user);

        // Сохраняем параметры оплаченной страховки в импланте.
        SaveInsuranceImplantTerms(
            installedImplant,
            machine,
            price);

        insurance.PurchaseCount++;

        return true;
    }

    private void PayHospitalReward(long paidPrice)
    {
        var reward = paidPrice / 10;

        Log.Warning(
            $"[Insurance] Paid price: {paidPrice}, " +
            $"Hospital reward: {reward}");

        if (reward <= 0)
            return;

        if (!_bank.TryGetBalance(
                SectorBankAccount.Medical,
                out var before))
        {
            Log.Error("[Insurance] Medical account not found!");
            return;
        }

        Log.Warning(
            $"[Insurance] Hospital balance before: {before}");

        var success = _bank.TrySectorDeposit(
            SectorBankAccount.Medical,
            reward,
            LedgerEntryType.StationDepositOther);

        if (!success)
        {
            Log.Error("[Insurance] Hospital deposit failed!");
            return;
        }

        if (_bank.TryGetBalance(
                SectorBankAccount.Medical,
                out var after))
        {
            Log.Warning(
                $"[Insurance] Hospital balance after: {after}");
        }
    }

    public async Task<bool> TryPurchaseAsync(
    EntityUid user,
    BodyInsuranceMachineComponent machine)
    {
        if (!_mobState.IsAlive(user) || IsInsured(user))
            return false;

        if (!TryComp<ActorComponent>(user, out var actor))
            return false;

        var account = actor.PlayerSession.UserId;

        if (!_pendingPurchases.Add(account))
            return false;

        var round = _subscriptionRound;
        var subscriptionPrice = machine.SubscriptionCost;
        var orderId = $"body-insurance:{Guid.NewGuid():N}";

        var charged = false;
        var completed = false;

        try
        {
            var price = GetPrice(user, machine);

            if (!_bank.TryGetBalance(user, out var balance)
                || balance < price)
                return false;

            if (!_roundSubscriptions.Contains(account))
            {
                var result = await _lagoonCoins.Spend(
                    account,
                    subscriptionPrice,
                    orderId,
                    "Подписка на страхование тела");

                if (!result.Applied ||
                    result.Awarded != -subscriptionPrice)
                    return false;

                charged = true;
            }

            // За время обращения к БД мог закончиться
            // раунд или отключиться игрок.
            if (round != _subscriptionRound ||
                !Exists(user) ||
                !TryComp<ActorComponent>(user, out var currentActor) ||
                currentActor.PlayerSession.UserId != account)
                return false;

            if (!TryPurchaseInternal(user, machine))
                return false;

            _roundSubscriptions.Add(account);
            completed = true;

            // Начисление госпиталю после успешной покупки.
            if (TryComp<BodyInsuranceComponent>(user, out var insurance))
            {
                PayHospitalReward(insurance.PaidInsurancePrice);
            }

            return true;
        }
        catch (InsufficientLagoonCoinsException)
        {
            if (Exists(user))
            {
                _popup.PopupEntity(
                    "Недостаточно LC Coins для подписки.",
                    user,
                    user);
            }

            return false;
        }
        catch (Exception e)
        {
            Log.Error($"BodyInsurance purchase failed: {e}");
            return false;
        }
        finally
        {
            try
            {
                // Возвращаем LC, если страховка
                // так и не была приобретена.
                if (charged && !completed)
                {
                    await _lagoonCoins.Grant(
                        account,
                        subscriptionPrice,
                        "Возврат оплаты страховки тела",
                        null,
                        $"refund:{orderId}");
                }
            }
            finally
            {
                _pendingPurchases.Remove(account);
            }
        }
    }

    public void RegisterDeath(EntityUid user)
    {
        if (!TryComp<BodyInsuranceComponent>(
                user, out var insurance))
            return;

        if (insurance.DeathTime != null)
            return;

        insurance.DeathCount++;
        insurance.DeathTime = _timing.CurTime;

        if (!insurance.Insured)
            return;

        if (insurance.ReturnAction is not { } action)
            return;

        _actions.SetEnabled(action, true);

        _actions.SetCooldown(
            action,
            _timing.CurTime,
            _timing.CurTime +
            TimeSpan.FromSeconds(insurance.ReturnDelay));
    }

    public void RegisterRevival(EntityUid user)
    {
        if (!TryComp<BodyInsuranceComponent>(
                user, out var insurance))
            return;

        insurance.DeathTime = null;

        if (insurance.ReturnAction is not { } action)
            return;

        _actions.SetEnabled(action, false);
    }

    public bool TryExpeditionRescue(
    EntityUid body,
    EntityUid shuttle,
    EntityCoordinates destination)
    {
        if (!TryComp<BodyInsuranceComponent>(
                body, out var insurance))
            return false;

        if (!insurance.Insured)
            return false;

        if (TerminatingOrDeleted(body) ||
            TerminatingOrDeleted(shuttle))
            return false;

        // Проверяем, что точка назначения находится на шаттле.
        if (destination.EntityId != shuttle)
            return false;

        // Освобождаем персонажа из старого контейнера.
        if (_containers.TryGetContainingContainer(
                body, out var container))
        {
            if (!_containers.Remove(
                    body, container, force: true))
                return false;
        }

        // Создаём эвакуационный мешок на шаттле
        // и помещаем в него персонажа.
        if (!TryEvacuateIntoBag(body, destination))
        {
            Log.Warning(
                $"BodyInsurance: Expedition bag rescue failed for {body}.");

            return false;
        }

        // Удаляем снаряжение.
        RemoveEvacuationEquipment(body);

        // Расходуем страховку.
        ConsumeInsurance(body);

        // Наносим 200 единиц тупого урона.
        var damage = new DamageSpecifier();

        damage.DamageDict["Blunt"] =
            FixedPoint2.New(insurance.ExpeditionRescueDamage);

        _damage.TryChangeDamage(
            body,
            damage,
            ignoreResistances: true,
            interruptsDoAfters: false,
            canSever: false,
            originFlag:
                DamageableSystem.DamageOriginFlag.BodyInsurance);

        // Включаем датчики координат.
        EnableRescueSensors(body);

        // Воспроизводим анимацию и звук.
        if (TryComp<ActorComponent>(body, out var actor))
        {
            RaiseNetworkEvent(
                new BodyInsuranceBiteEvent(),
                actor.PlayerSession);
        }

        Log.Info(
            $"BodyInsurance: {body} rescued into bag on shuttle {shuttle}.");

        return true;
    }

    public bool HasReturnDelayPassed(
        EntityUid user,
        float delay = 600f)
    {
        if (!TryComp<BodyInsuranceComponent>(user, out var insurance))
            return false;

        if (!insurance.Insured)
            return false;

        if (!_mobState.IsDead(user))
            return false;

        if (insurance.DeathTime is not { } deathTime)
            return false;

        return _timing.CurTime - deathTime >=
               TimeSpan.FromSeconds(delay);
    }

    private readonly HashSet<NetUserId> _roundSubscriptions = new();

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _roundSubscriptions.Clear();
        _subscriptionRound = Guid.NewGuid();
        _nextProximityCheck = TimeSpan.Zero;
    }

    public bool HasSubscription(EntityUid user)
    {
        return TryComp<ActorComponent>(user, out var actor)
            && _roundSubscriptions.Contains(actor.PlayerSession.UserId);
    }

    public int GetSubscriptionPrice(
        EntityUid user,
        BodyInsuranceMachineComponent machine)
    {
        return HasSubscription(user)
            ? 0
            : machine.SubscriptionCost;
    }
    public bool ConsumeInsurance(EntityUid user)
    {
        if (!TryComp<BodyInsuranceComponent>(
                user, out var insurance))
            return false;

        if (!insurance.Insured)
            return false;

        insurance.Insured = false;

        // Растворяем страховой имплант.
        var implant = insurance.InsuranceImplant;

        insurance.InsuranceImplant = null;

        if (implant is { } implantUid &&
            !TerminatingOrDeleted(implantUid))
        {
            QueueDel(implantUid);
        }

        if (insurance.ReturnAction is { } action)
        {
            insurance.ReturnAction = null;

            if (!TerminatingOrDeleted(action))
            {
                _actions.RemoveAction(user, action);
                QueueDel(action);
            }
        }

        return true;
    }
}

using Content.Client.UserInterface;
using Content.Shared.Power;
using JetBrains.Annotations;
using Robust.Client.Timing;
using Robust.Client.UserInterface;

namespace Content.Client.Power.Battery;

/// <summary>
/// BUI for <see cref="BatteryUiKey.Key"/>.
/// </summary>
/// <seealso cref="BoundUserInterfaceState"/>
/// <seealso cref="BatteryMenu"/>
[UsedImplicitly]
public sealed partial class BatteryBoundUserInterface : BoundUserInterface, IBuiPreTickUpdate
{
    [Dependency] private IClientGameTiming _gameTiming = null!;

    [ViewVariables]
    private BatteryMenu? _menu;

    private BuiPredictionState? _pred;
    private InputCoalescer<float> _chargeRateCoalescer;
    private InputCoalescer<float> _dischargeRateCoalescer;

    public BatteryBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
        IoCManager.InjectDependencies(this);
    }

    protected override void Open()
    {
        base.Open();

        _pred = new BuiPredictionState(this, _gameTiming);
        _chargeRateCoalescer = default;
        _dischargeRateCoalescer = default;

        if (OpenTgui())
            return;

        OpenClassic();
    }

    private void OpenClassic()
    {
        _menu = this.CreateWindow<BatteryMenu>();
        _menu.SetEntity(Owner);

        _menu.OnInBreaker += val => _pred!.SendMessage(new BatterySetInputBreakerMessage(val));
        _menu.OnOutBreaker += val => _pred!.SendMessage(new BatterySetOutputBreakerMessage(val));

        _menu.OnChargeRate += val => _chargeRateCoalescer.Set(val);
        _menu.OnDischargeRate += val => _dischargeRateCoalescer.Set(val);
        if (_latestState != null)
            _menu.Update(_latestState);
    }

    void IBuiPreTickUpdate.PreTickUpdate()
    {
        if (_chargeRateCoalescer.CheckIsModified(out var chargeRateValue))
            _pred!.SendMessage(new BatterySetChargeRateMessage(chargeRateValue));

        if (_dischargeRateCoalescer.CheckIsModified(out var dischargeRateValue))
            _pred!.SendMessage(new BatterySetDischargeRateMessage(dischargeRateValue));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is not BatteryBuiState batteryState)
            return;

        foreach (var replayMsg in _pred!.MessagesToReplay())
        {
            switch (replayMsg)
            {
                case BatterySetInputBreakerMessage setInputBreaker:
                    batteryState.CanCharge = setInputBreaker.On;
                    break;

                case BatterySetOutputBreakerMessage setOutputBreaker:
                    batteryState.CanDischarge = setOutputBreaker.On;
                    break;

                case BatterySetChargeRateMessage setChargeRate:
                    batteryState.MaxChargeRate = setChargeRate.Rate;
                    break;

                case BatterySetDischargeRateMessage setDischargeRate:
                    batteryState.MaxSupply = setDischargeRate.Rate;
                    break;
            }
        }

        // A WebView input can arrive between network state delivery and the next
        // prediction tick. Keep the unsent coalesced value until that tick sends it.
        if (_chargeRateCoalescer.IsModified)
            batteryState.MaxChargeRate = _chargeRateCoalescer.LastValue;
        if (_dischargeRateCoalescer.IsModified)
            batteryState.MaxSupply = _dischargeRateCoalescer.LastValue;

        _latestState = batteryState;
        _menu?.Update(batteryState);
        PublishTgui();
    }
}

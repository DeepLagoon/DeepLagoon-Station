using Content.Server.Construction;
using Content.Server.Construction.Components;
using Content.Server.Power.Components;
using Content.Server._NF.Power.Components;
using Content.Shared.Power.Generator;

namespace Content.Server._NF.Power.EntitySystems;

/// <summary>
/// This handles using upgraded machine parts
/// to modify the power supply/generation of a machine.
/// </summary>
public sealed partial class UpgradePowerSystem : EntitySystem
{
    [Dependency] private IComponentFactory _componentFactory = default!;

    /// <inheritdoc/>
    public override void Initialize()
    {
        SubscribeLocalEvent<UpgradePowerDrawComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<UpgradePowerDrawComponent, RefreshPartsEvent>(OnRefreshParts);
        SubscribeLocalEvent<UpgradePowerDrawComponent, UpgradeExamineEvent>(OnUpgradeExamine);

        SubscribeLocalEvent<UpgradePowerSupplierComponent, MapInitEvent>(OnSupplierMapInit);
        SubscribeLocalEvent<UpgradePowerSupplierComponent, RefreshPartsEvent>(OnSupplierRefreshParts);
        SubscribeLocalEvent<UpgradePowerSupplierComponent, UpgradeExamineEvent>(OnSupplierUpgradeExamine);

        SubscribeLocalEvent<UpgradePowerSupplyRampingComponent, MapInitEvent>(OnSupplyRampingMapInit);
        SubscribeLocalEvent<UpgradePowerSupplyRampingComponent, RefreshPartsEvent>(OnSupplyRampingRefreshParts);
        SubscribeLocalEvent<UpgradePowerSupplyRampingComponent, UpgradeExamineEvent>(OnSupplyRampingUpgradeExamine);
    }

    private void OnMapInit(EntityUid uid, UpgradePowerDrawComponent component, MapInitEvent args)
    {
        if (TryComp<PowerConsumerComponent>(uid, out var powa))
            component.BaseLoad = powa.DrawRate;
        else if (TryComp<ApcPowerReceiverComponent>(uid, out var powa2))
            component.BaseLoad = powa2.Load;
    }

    internal void RestoreUpgradeBases(EntityUid uid)
    {
        if (Prototype(uid) is not { } prototype)
            return;

        // The saved power components already contain the upgraded values. Start from the prototype
        // before refreshing the installed parts so repeated parking cannot compound the upgrades.
        if (TryComp<UpgradePowerDrawComponent>(uid, out var draw))
        {
            if (prototype.TryComp<PowerConsumerComponent>(out var consumer, _componentFactory))
                draw.BaseLoad = consumer.DrawRate;
            else if (prototype.TryComp<ApcPowerReceiverComponent>(out var receiver, _componentFactory))
                draw.BaseLoad = receiver.Load;
        }

        if (TryComp<UpgradePowerSupplierComponent>(uid, out var supply) &&
            prototype.TryComp<PowerSupplierComponent>(out var supplier, _componentFactory))
            supply.BaseSupplyRate = supplier.MaxSupply;

        if (TryComp<UpgradePowerSupplyRampingComponent>(uid, out var ramp) &&
            prototype.TryComp<PowerNetworkBatteryComponent>(out var battery, _componentFactory))
            ramp.BaseRampRate = battery.SupplyRampRate;
    }

    private void OnRefreshParts(EntityUid uid, UpgradePowerDrawComponent component, RefreshPartsEvent args)
    {
        var load = component.BaseLoad;
        var rating = args.PartRatings[component.MachinePartPowerDraw];
        switch (component.Scaling)
        {
            case MachineUpgradeScalingType.Linear:
                load += component.PowerDrawMultiplier * (rating - 1);
                break;
            case MachineUpgradeScalingType.Exponential:
                load *= MathF.Pow(component.PowerDrawMultiplier, rating - 1);
                break;
            default:
                Log.Error($"invalid power scaling type for {ToPrettyString(uid)}.");
                load = 0;
                break;
        }
        if (TryComp<ApcPowerReceiverComponent>(uid, out var powa))
            powa.Load = load;
        if (TryComp<PowerConsumerComponent>(uid, out var powa2))
            powa2.DrawRate = load;
    }

    private void OnUpgradeExamine(EntityUid uid, UpgradePowerDrawComponent component, UpgradeExamineEvent args)
    {
        // UpgradePowerDrawComponent.PowerDrawMultiplier is not the actual multiplier, so we have to do this.
        var powerDrawMultiplier = CompOrNull<ApcPowerReceiverComponent>(uid)?.Load / component.BaseLoad
            ?? CompOrNull<PowerConsumerComponent>(uid)?.DrawRate / component.BaseLoad;

        if (powerDrawMultiplier is not null)
            args.AddPercentageUpgrade("upgrade-power-draw", powerDrawMultiplier.Value);
    }

    private void OnSupplierMapInit(EntityUid uid, UpgradePowerSupplierComponent component, MapInitEvent args)
    {
        if (TryComp<PowerSupplierComponent>(uid, out var supplier))
            component.BaseSupplyRate = supplier.MaxSupply;
    }

    private void OnSupplierRefreshParts(EntityUid uid, UpgradePowerSupplierComponent component, RefreshPartsEvent args)
    {
        var rating = args.PartRatings[component.MachinePartPowerSupply];
        switch (component.Scaling)
        {
            case MachineUpgradeScalingType.Linear:
                component.ActualScalar = 1f + component.PowerSupplyMultiplier * (rating - 1);
                break;
            case MachineUpgradeScalingType.Exponential:
                component.ActualScalar = MathF.Pow(component.PowerSupplyMultiplier, rating - 1);
                break;
            default:
                Log.Error($"invalid power scaling type for {ToPrettyString(uid)}.");
                component.ActualScalar = 1f;
                break;
        }

        // Fuel generators calculate their output from the target power and multiplier each tick.
        if (!HasComp<FuelGeneratorComponent>(uid) && TryComp<PowerSupplierComponent>(uid, out var supplier))
            supplier.MaxSupply = component.BaseSupplyRate * component.ActualScalar;
    }

    private void OnSupplierUpgradeExamine(EntityUid uid, UpgradePowerSupplierComponent component, UpgradeExamineEvent args)
    {
        args.AddPercentageUpgrade("upgrade-power-supply", component.ActualScalar);
    }

    private void OnSupplyRampingMapInit(EntityUid uid, UpgradePowerSupplyRampingComponent component, MapInitEvent args)
    {
        if (TryComp<PowerNetworkBatteryComponent>(uid, out var battery))
            component.BaseRampRate = battery.SupplyRampRate;
    }

    private void OnSupplyRampingRefreshParts(EntityUid uid, UpgradePowerSupplyRampingComponent component, RefreshPartsEvent args)
    {
        var rampRate = component.BaseRampRate;
        var rating = args.PartRatings[component.MachinePartRampRate];
        switch (component.Scaling)
        {
            case MachineUpgradeScalingType.Linear:
                rampRate += component.SupplyRampingMultiplier * component.BaseRampRate * (rating - 1);
                break;
            case MachineUpgradeScalingType.Exponential:
                rampRate *= MathF.Pow(component.SupplyRampingMultiplier, rating - 1);
                break;
            default:
                Log.Error($"invalid power supply ramping type for {ToPrettyString(uid)}.");
                rampRate = component.BaseRampRate;
                break;
        }

        component.ActualScalar = rampRate / component.BaseRampRate;

        if (TryComp<PowerNetworkBatteryComponent>(uid, out var battery))
            battery.SupplyRampRate = rampRate;
    }

    private void OnSupplyRampingUpgradeExamine(EntityUid uid, UpgradePowerSupplyRampingComponent component, UpgradeExamineEvent args)
    {
        args.AddPercentageUpgrade("upgrade-power-supply-ramping", component.ActualScalar);
    }
}

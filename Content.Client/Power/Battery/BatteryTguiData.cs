using Content.Client._DeepLagoon.WebUI;
using Content.Shared.Power;

namespace Content.Client.Power.Battery;

/// <summary>Uses the same units, rounding and ETA semantics as the native battery menu.</summary>
public static class BatteryTguiData
{
    public static TguiData Labels()
    {
        var labels = new TguiData();
        foreach (var key in new[]
                 {
                     "substation", "smes", "substation-model", "smes-model", "substation-description", "smes-description",
                     "stored", "energy", "charge", "discharge", "input", "output", "network", "switches", "on", "off",
                     "charge-limit", "discharge-limit", "load", "through", "high-voltage", "maintenance", "section",
                     "waiting", "watts", "kilowatts", "megawatts", "gigawatts", "network-powered", "network-unpowered",
                 })
            labels.String(key, Loc.GetString("dl-battery-" + key));
        return labels;
    }

    public static TguiData Build(BatteryBuiState? state, string title, bool smes)
    {
        var result = new TguiData().String("title", title).Bool("smes", smes).Bool("ready", state != null);
        if (state == null)
            return result;

        var delta = state.CurrentReceiving - state.CurrentSupply;
        if (Math.Abs(delta) < state.Capacity / 100_000)
            delta = 0;
        var fraction = state.Capacity > 0 ? Math.Clamp(state.Charge / state.Capacity, 0, 1) : 0;
        var etaDifference = delta > 0 ? (state.Capacity - state.Charge) / state.Efficiency : -state.Charge;
        var minutes = etaDifference / delta / 60.0;
        var eta = !double.IsFinite(minutes) || Math.Abs(minutes) > 1000 || Math.Abs(minutes) < 0.01
            ? Loc.GetString("battery-menu-eta-value-na")
            : Loc.GetString(minutes > 60 ? "battery-menu-eta-value-max" : "battery-menu-eta-value",
                ("minutes", Math.Min(Math.Ceiling(minutes), 60)));

        return result.Bool("input", state.CanCharge).Bool("output", state.CanDischarge)
            .Bool("inputPowered", state.SupplyingNetworkHasPower).Bool("outputPowered", state.LoadingNetworkHasPower)
            .Number("fraction", fraction)
            .String("percentText", Loc.GetString("battery-menu-stored-percent-value", ("value", fraction)))
            .String("energyText", Loc.GetString("battery-menu-stored-energy-value", ("value", state.Charge)))
            .String("etaLabel", Loc.GetString(delta > 0 ? "battery-menu-eta-full" : "battery-menu-eta-empty"))
            .String("etaText", eta)
            .String("inputText", Power(state.CurrentReceiving)).String("outputText", Power(state.CurrentSupply))
            .String("chargeText", Power(Math.Max(0, delta))).String("dischargeText", Power(Math.Max(0, -delta)))
            .String("chargeLimitText", Power(state.MaxChargeRate)).String("dischargeLimitText", Power(state.MaxSupply))
            .String("passthroughText", Power(Math.Min(state.CurrentReceiving, state.CurrentSupply)))
            .Number("passthrough", Math.Max(0, Math.Min(state.CurrentReceiving, state.CurrentSupply)))
            .Number("chargeLimit", state.MaxChargeRate).Number("chargeMin", state.MinMaxChargeRate).Number("chargeMax", state.MaxMaxChargeRate)
            .Number("dischargeLimit", state.MaxSupply).Number("dischargeMin", state.MinMaxSupply).Number("dischargeMax", state.MaxMaxSupply);
    }

    private static string Power(float value) => Loc.GetString("battery-menu-power-value", ("value", value));
}

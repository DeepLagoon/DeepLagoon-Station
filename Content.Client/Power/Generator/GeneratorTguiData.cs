using Content.Client._DeepLagoon.WebUI;
using Content.Shared.Power.Generator;

namespace Content.Client.Power.Generator;

/// <summary>Retains native generator fuel units, efficiency and network-stat ordering.</summary>
public static class GeneratorTguiData
{
    public static TguiData Labels()
    {
        var labels = new TguiData();
        foreach (var key in new[] { "target", "setpoint", "efficiency", "eta", "state", "controls", "output", "fuel", "remaining",
                     "fuel-use", "network", "maintenance", "section", "waiting", "kilowatts", "line-lock" })
            labels.String(key, Loc.GetString("dl-generator-" + key));
        foreach (var key in new[] { "start", "stop", "eject", "clogged", "unanchored", "status-starting", "status-stopped", "status-running" })
            labels.String(key, Loc.GetString("portable-generator-ui-" + key));
        return labels;
    }

    public static TguiData Build(PortableGeneratorComponentBuiState? state, FuelGeneratorComponent? fuel,
        string title, string style, string fuelLabel, bool anchored, bool starting, float progress)
    {
        var data = new TguiData().String("title", title).String("style", style)
            .String("fuelLabel", Loc.GetString(fuelLabel)).Bool("ready", state != null && fuel != null)
            .Bool("anchored", anchored).Bool("starting", starting).Number("startProgress", Math.Clamp(progress, 0, 1));
        if (state == null || fuel == null)
            return data;
        var efficiency = SharedGeneratorSystem.CalcFuelEfficiency(state.TargetPower, state.OptimalPower, fuel);
        var minutes = state.RemainingFuel / (fuel.OptimalBurnRate / efficiency) / 60.0;
        return data.Bool("on", state.On).Bool("clogged", state.Clogged)
            .Number("target", state.TargetPower / 1000).Number("max", state.MaximumPower / 1000)
            .Number("min", Math.Min(fuel.MinTargetPower, state.MaximumPower) / 1000)
            .String("efficiencyText", float.IsFinite(efficiency) ? efficiency.ToString("P1") : "—")
            .String("etaText", double.IsFinite(minutes)
                ? Loc.GetString("portable-generator-ui-eta", ("minutes", Math.Ceiling(minutes))) : "—")
            .Number("fuelFraction", state.RemainingFuel - MathF.Floor(state.RemainingFuel))
            .String("fuelText", MathF.Floor(state.RemainingFuel).ToString())
            .Bool("networkConnected", state.NetworkStats != null)
            .Bool("networkGood", state.NetworkStats is { } net && net.Load <= net.Supply)
            .String("networkText", state.NetworkStats is { } stats
                ? Loc.GetString("portable-generator-ui-network-stats-value", ("load", stats.Load), ("supply", stats.Supply))
                : Loc.GetString("portable-generator-ui-network-stats-not-connected"));
    }
}

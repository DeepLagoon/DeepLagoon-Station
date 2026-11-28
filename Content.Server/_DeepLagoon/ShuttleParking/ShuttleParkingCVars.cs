using Robust.Shared.Configuration;

namespace Content.Server._DeepLagoon.ShuttleParking;

[CVarDefs]
public sealed class ShuttleParkingCVars
{
    public static readonly CVarDef<bool> RepairEnabled = CVarDef.Create("parking.repair_enabled", false, CVar.SERVERONLY,
        "Enable paid shipyard repairs and the repair tab in parking consoles. Existing paid jobs finish normally.");
    public static readonly CVarDef<bool> AutoDebug = CVarDef.Create("parking.auto_debug", false, CVar.SERVERONLY);
    public static readonly CVarDef<float> RepairCommission = CVarDef.Create("parking.repair_commission", .25f, CVar.SERVERONLY,
        "Shipyard commission as a fraction of the current market material bill; 0.25 means 25 percent.");
    public static readonly CVarDef<int> RepairCreditsPerCoin = CVarDef.Create("parking.repair_credits_per_lc", 1000, CVar.SERVERONLY,
        "Credit-equivalent repair cost per LC for instant shipyard service, rounded up.");

    /// <summary>A positive value enables diagnostics and overrides the normal parking delay.</summary>
    public static readonly CVarDef<int> AutoDebugSeconds = CVarDef.Create("parking.auto_debug_seconds", 0, CVar.SERVERONLY);
}

using Robust.Shared.Configuration;

namespace Content.Server._DeepLagoon.ShuttleParking;

[CVarDefs]
public sealed class ShuttleParkingCVars
{
    public static readonly CVarDef<bool> AutoDebug = CVarDef.Create("parking.auto_debug", false, CVar.SERVERONLY);

    /// <summary>A positive value enables diagnostics and overrides the normal parking delay.</summary>
    public static readonly CVarDef<int> AutoDebugSeconds = CVarDef.Create("parking.auto_debug_seconds", 0, CVar.SERVERONLY);
}

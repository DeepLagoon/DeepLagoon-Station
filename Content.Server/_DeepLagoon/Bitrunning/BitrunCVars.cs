using Robust.Shared.Configuration;

namespace Content.Server._DeepLagoon.Bitrunning;

[CVarDefs]
public sealed class BitrunCVars
{
    public static readonly CVarDef<int> LobbyIntervalSeconds = CVarDef.Create("bitrun.lobby_interval_seconds", 180, CVar.SERVERONLY);
}

using Robust.Shared.Configuration;
namespace Content.Server._DeepLagoon.Voyages;

[CVarDefs]
public sealed class SectorResearchSettings
{
    // Zero keeps the difficulty/crew formula. Changes only affect newly reserved orders.
    public static readonly CVarDef<int> Budget = CVarDef.Create("sector_expeditions.research_budget", 0, CVar.SERVERONLY);
    public static readonly CVarDef<int> RefreshSeconds = CVarDef.Create("sector_expeditions.research_refresh_seconds", 900, CVar.SERVERONLY);
}

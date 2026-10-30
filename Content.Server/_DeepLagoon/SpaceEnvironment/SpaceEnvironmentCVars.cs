using Robust.Shared.Configuration;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

[CVarDefs]
public sealed class SpaceEnvironmentCVars
{
    public static readonly CVarDef<bool> Enabled = CVarDef.Create("sector.environment_enabled", true, CVar.SERVERONLY);
    public static readonly CVarDef<bool> DebrisEnabled = CVarDef.Create("sector.orbital_debris", true, CVar.SERVERONLY);
    public static readonly CVarDef<string> Campaign = CVarDef.Create("sector.campaign", "frontier", CVar.SERVERONLY);
    public static readonly CVarDef<int> MaxLoaded = CVarDef.Create("sector.max_loaded", 8, CVar.SERVERONLY);
    public static readonly CVarDef<int> Seed = CVarDef.Create("sector.world_seed", 20261029, CVar.SERVERONLY);
    public static readonly CVarDef<int> SurveyResearchBase = CVarDef.Create("sector.survey_research_base", SpaceSurveyResearchReward.DefaultBasePoints, CVar.SERVERONLY);
}

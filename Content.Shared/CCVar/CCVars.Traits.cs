using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    public static readonly CVarDef<bool> GameTraitsEnabled =
        CVarDef.Create("game.traits_enabled", true, CVar.SERVER | CVar.REPLICATED);
    public static readonly CVarDef<int> GameTraitsDefaultPoints =
        CVarDef.Create("game.traits_default_points", 10, CVar.SERVER | CVar.REPLICATED);
    public static readonly CVarDef<int> GameTraitsMax =
        CVarDef.Create("game.traits_max", 14, CVar.SERVER | CVar.REPLICATED);
    public static readonly CVarDef<bool> RestrictedGearEnabled =
        CVarDef.Create("trait.restrictedgear_enabled", true, CVar.SERVER | CVar.REPLICATED);
}

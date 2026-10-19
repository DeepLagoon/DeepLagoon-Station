using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    public static readonly CVarDef<string> WikiOrigin = CVarDef.Create(
        "wiki.origin", "https://wiki.deep-lagoon-ss14.ru", CVar.SERVER | CVar.REPLICATED);
    public static readonly CVarDef<string> WikiRoutes = CVarDef.Create(
        "wiki.routes", "", CVar.SERVER | CVar.REPLICATED);
    public static readonly CVarDef<string> WikiTreeRoot = CVarDef.Create(
        "wiki.tree_root", "/ru/in_game", CVar.SERVER | CVar.REPLICATED);
    public static readonly CVarDef<string> WikiTreeLocale = CVarDef.Create(
        "wiki.tree_locale", "ru", CVar.SERVER | CVar.REPLICATED);
    public static readonly CVarDef<float> WikiNewPlayerMinutes = CVarDef.Create(
        "wiki.new_player_minutes", 180f, CVar.SERVER | CVar.REPLICATED);
}

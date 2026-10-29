using Robust.Shared.Configuration;

namespace Content.Shared._DeepLagoon.InteractionPanel;

[CVarDefs]
// Legacy-ключи конфигурации сохраняют избранное и интервал существующих игроков.
// Имена C# переименованы; строки хранилища намеренно стабильны.
public sealed class InteractionPanelCVars
{
    public static readonly CVarDef<string> Favorites = CVarDef.Create("deeplagoon.social_favorites", "", CVar.CLIENTONLY | CVar.ARCHIVE);
    public static readonly CVarDef<float> Interval = CVarDef.Create("deeplagoon.social_interval", 3f, CVar.CLIENTONLY | CVar.ARCHIVE);
    public static readonly CVarDef<bool> AutomaticMoans = CVarDef.Create("deeplagoon.automatic_moans", true, CVar.CLIENT | CVar.ARCHIVE | CVar.REPLICATED);
    public static readonly CVarDef<float> MoanChance = CVarDef.Create("deeplagoon.moan_chance", 20f, CVar.CLIENT | CVar.ARCHIVE | CVar.REPLICATED);
    public static readonly CVarDef<bool> CustomMoanChance = CVarDef.Create("deeplagoon.custom_moan_chance", false, CVar.CLIENT | CVar.ARCHIVE | CVar.REPLICATED);
    public static readonly CVarDef<bool> NeutralArousal = CVarDef.Create("deeplagoon.neutral_arousal", false, CVar.CLIENT | CVar.ARCHIVE | CVar.REPLICATED);
    public static readonly CVarDef<float> ArousalMultiplier = CVarDef.Create("deeplagoon.arousal_multiplier", 100f, CVar.CLIENT | CVar.ARCHIVE | CVar.REPLICATED);
}

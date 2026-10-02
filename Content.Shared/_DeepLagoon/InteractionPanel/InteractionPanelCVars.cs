using Robust.Shared.Configuration;

namespace Content.Shared._DeepLagoon.InteractionPanel;

[CVarDefs]
// Legacy-ключи конфигурации сохраняют избранное и интервал существующих игроков.
// Имена C# переименованы; строки хранилища намеренно стабильны.
public sealed class InteractionPanelCVars
{
    public static readonly CVarDef<string> Favorites = CVarDef.Create("deeplagoon.social_favorites", "", CVar.CLIENTONLY | CVar.ARCHIVE);
    public static readonly CVarDef<float> Interval = CVarDef.Create("deeplagoon.social_interval", 3f, CVar.CLIENTONLY | CVar.ARCHIVE);
}


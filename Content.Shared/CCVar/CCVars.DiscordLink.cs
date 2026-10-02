using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    public static readonly CVarDef<bool> DiscordLinkEnabled =
        CVarDef.Create("discord_link.enabled", false, CVar.SERVERONLY);

    public static readonly CVarDef<string> DiscordLinkToken =
        CVarDef.Create("discord_link.api_token", "", CVar.SERVERONLY | CVar.CONFIDENTIAL);
}

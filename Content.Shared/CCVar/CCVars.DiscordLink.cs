using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    public static bool DiscordAdmissionRequired(IConfigurationManager configuration)
    {
#if DEVELOPMENT
        return false;
#else
        return configuration.GetCVar(DiscordLinkEnabled);
#endif
    }

    public static bool DiscordAdmissionDevelopment =>
#if DEVELOPMENT
        true;
#else
        false;
#endif

    public static readonly CVarDef<bool> DiscordLinkEnabled =
        CVarDef.Create("discord_link.enabled", false, CVar.SERVERONLY);

    public static readonly CVarDef<bool> DiscordExternalAccounts =
        CVarDef.Create("discord_link.external_accounts", false, CVar.SERVERONLY);

    public static readonly CVarDef<string> DiscordLinkDatabaseFile =
        CVarDef.Create("discord_link.database_file", "discord-links.db", CVar.SERVERONLY);

    public static readonly CVarDef<string> DiscordLinkToken =
        CVarDef.Create("discord_link.api_token", "", CVar.SERVERONLY | CVar.CONFIDENTIAL);

    public static readonly CVarDef<string> DiscordSupporterToken =
        CVarDef.Create("discord_link.supporter_token", "", CVar.SERVERONLY | CVar.CONFIDENTIAL);
    public static readonly CVarDef<string> DiscordModerationToken =
        CVarDef.Create("discord_link.moderation_token", "", CVar.SERVERONLY | CVar.CONFIDENTIAL);
    public static readonly CVarDef<string> DiscordCoinToken =
        CVarDef.Create("discord_link.coin_token", "", CVar.SERVERONLY | CVar.CONFIDENTIAL);
    public static readonly CVarDef<string> DiscordIdentityToken =
        CVarDef.Create("discord_link.identity_token", "", CVar.SERVERONLY | CVar.CONFIDENTIAL);

    public static readonly CVarDef<string> DiscordAccountMergePython =
        CVarDef.Create("discord_link.merge_python", "/home/ss14/ss14/control-agent-venv/bin/python", CVar.SERVERONLY);

    public static readonly CVarDef<string> DiscordAccountMergeWorker =
        CVarDef.Create("discord_link.merge_worker", "/opt/lagoon-launcher/identity/merge_game_accounts.py", CVar.SERVERONLY);
}

using System.IO;
using Content.Shared.CCVar;

namespace Content.Server._DeepLagoon.DiscordLink;

public sealed partial class DiscordLinkSystem
{
    private DiscordLinkStore? _ahelpReadOnlyLinks;

    // Ahelp can use existing verified identities on slots that do not require
    // linking to enter the game. The admission and enrollment APIs remain independent.
    private DiscordLinkStore? AhelpLinks()
    {
        if (_store != null)
            return _store;
        if (_ahelpReadOnlyLinks != null)
            return _ahelpReadOnlyLinks;
        if (_resources.UserData.RootDir is not { } root)
            return null;
        var path = DiscordLinkStore.ResolvePath(root, _config.GetCVar(CCVars.DiscordLinkDatabaseFile));
        if (File.Exists(path))
            _ahelpReadOnlyLinks = new DiscordLinkStore(path, readOnly: true);
        return _ahelpReadOnlyLinks;
    }

    internal (Guid Uid, string Name)? AhelpIdentity(string discord)
        => AhelpLinks() is { } store && store.FindDiscord(discord) is { } link && !store.MergePending(link.Uid)
            ? (link.Uid, link.Username)
            : null;

    internal string? AhelpDiscordId(Guid uid)
        => AhelpLinks()?.FindDiscordId(uid);

    internal Guid[] AhelpUserIds(string discord) => AhelpLinks()?.AhelpUserIds(discord) ?? [];
}

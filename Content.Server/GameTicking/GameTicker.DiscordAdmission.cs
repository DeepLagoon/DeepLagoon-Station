using Content.Server._DeepLagoon.DiscordLink;
using Robust.Shared.Player;

namespace Content.Server.GameTicking;

public sealed partial class GameTicker
{
    private DiscordLinkSystem DiscordAdmission => EntityManager.System<DiscordLinkSystem>();

    private bool CheckDiscordRoundAdmission(ICommonSession session)
    {
        if (DiscordAdmission.CanEnterRound(session))
            return true;
        _chatManager.DispatchServerMessage(session, DiscordAdmission.AdmissionMessage(session));
        return false;
    }
}

using Content.Client._DeepLagoon.Ahelp;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Content.Shared._DeepLagoon.Ahelp;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Client.UserInterface.Systems.Bwoink;

public sealed partial class AHelpUIController
{
    private readonly HashSet<long> _seenTicketNotices = new();
    private AhelpWindow? _ticketWindow;
    private bool TicketMode => _config.GetCVar(CCVars.AhelpTickets);

    private void OpenTicketWindow(NetUserId? player = null)
    {
        if (_bwoinkSystem == null || _playerManager.LocalUser is not { } user)
            return;
        if (_ticketWindow != null)
        {
            if (player.HasValue) _ticketWindow.FocusPlayer(player.Value.UserId);
            return;
        }
        _ticketWindow = new AhelpWindow(_bwoinkSystem, user.UserId, _adminManager.HasFlag(AdminFlags.Adminhelp));
        _ticketWindow.TicketClosed += () =>
        {
            _ticketWindow = null;
            SetAHelpPressed(false);
        };
        _ticketWindow.OpenCentered();
        _ticketWindow.FocusPlayer(player?.UserId ?? Guid.Empty);
        SetAHelpPressed(true);
    }

    private void OnTicketNotice(AhelpNotice notice)
    {
        if (!TicketMode || !_seenTicketNotices.Add(notice.EventId))
            return;
        if (_seenTicketNotices.Count > 8192)
            _seenTicketNotices.Clear();
        if (notice.AdminSound && _bwoinkSoundEnabled)
        {
            _audio.PlayGlobal("/Audio/_DeepLagoon/Ahelp/oh-oh.ogg", Filter.Local(), false);
            _clyde.RequestWindowAttention();
        }
        if (notice.PlayerSound && _aHelpSound != null)
        {
            _audio.PlayGlobal(_aHelpSound, Filter.Local(), false);
            _clyde.RequestWindowAttention();
        }
        if (_ticketWindow == null && !notice.Remind)
            UnreadAHelpReceived();
    }
}

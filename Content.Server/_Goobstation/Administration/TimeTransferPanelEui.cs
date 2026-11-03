using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Server.Players.PlayTimeTracking;
using Content.Shared._Goobstation.Administration;
using Content.Shared.Administration;
using Content.Shared.Eui;
using System.Threading.Tasks;
using Content.Shared.Players.PlayTimeTracking;
using Robust.Shared.Prototypes;

namespace Content.Server._Goobstation.Administration;

public sealed partial class TimeTransferPanelEui : BaseEui
{
    [Dependency] private IAdminManager _adminMan = default!;
    [Dependency] private ILogManager _log = default!;
    [Dependency] private IPlayerLocator _playerLocator = default!;
    [Dependency] private IPrototypeManager _protoMan = default!;
    [Dependency] private PlayTimeTrackingManager _playTimeMan = default!;

    private readonly ISawmill _sawmill;
    private bool _transferring;

    public TimeTransferPanelEui()
    {
        IoCManager.InjectDependencies(this);

        _sawmill = _log.GetSawmill("admin.time_eui");
    }

    public override TimeTransferPanelEuiState GetNewState()
    {
        var hasFlag = _adminMan.HasAdminFlag(Player, AdminFlags.Moderator);

        return new TimeTransferPanelEuiState(hasFlag);
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (msg is not TimeTransferEuiMessage message)
            return;

        TransferTime(message.PlayerId, message.TimeData, message.Overwrite);
    }

    public async void TransferTime(string playerId, List<TimeTransferData> timeData, bool overwrite)
    {
        if (!_adminMan.HasAdminFlag(Player, AdminFlags.Moderator))
        {
            _sawmill.Warning($"{Player.Name} ({Player.UserId} tried to add roles time without moderator flag)");
            return;
        }

        if (_transferring)
            return;

        _transferring = true;
        try
        {
            await TransferTimeAsync(playerId, timeData, overwrite);
        }
        catch (Exception e)
        {
            _sawmill.Error($"Time transfer failed: {e}");
            SendMessage(new TimeTransferWarningEuiMessage(Loc.GetString("time-transfer-panel-warning-failed"), Color.Red));
        }
        finally
        {
            _transferring = false;
        }
    }

    private async Task TransferTimeAsync(string playerId, List<TimeTransferData> timeData, bool overwrite)
    {
        if (timeData.Count == 0)
        {
            SendMessage(new TimeTransferWarningEuiMessage(Loc.GetString("time-transfer-panel-warning-no-job"), Color.Red));
            return;
        }

        var times = new Dictionary<string, TimeSpan>();
        foreach (var data in timeData)
        {
            if (!_protoMan.HasIndex<PlayTimeTrackerPrototype>(data.PlaytimeTracker) ||
                !PlayTimeCommandUtilities.TryParseTime(data.TimeString, out var time))
            {
                SendMessage(new TimeTransferWarningEuiMessage(Loc.GetString("time-transfer-panel-warning-invalid-time"), Color.Red));
                return;
            }

            // Several jobs may share a tracker. Apply a group award only once.
            times[data.PlaytimeTracker] = time;
        }

        var playerData = await _playerLocator.LookupIdByNameOrIdAsync(playerId.Trim());
        if (playerData == null)
        {
            SendMessage(new TimeTransferWarningEuiMessage(Loc.GetString("time-transfer-panel-no-player-database-message"), Color.Red));
            return;
        }

        // Permissions may have changed while the player lookup was pending.
        if (!_adminMan.HasAdminFlag(Player, AdminFlags.Moderator))
            return;

        await _playTimeMan.UpdateTrackerTimesAsync(playerData.UserId, times, overwrite);
        _sawmill.Info($"{Player.Name} ({Player.UserId}) {(overwrite ? "set" : "added")} {times.Count} trackers for {playerData.UserId}");
        var success = overwrite ? "time-transfer-panel-warning-set-success" : "time-transfer-panel-warning-add-success";
        SendMessage(new TimeTransferWarningEuiMessage(Loc.GetString(success), Color.LightGreen));
    }

    public override void Opened()
    {
        base.Opened();
        _adminMan.OnPermsChanged += OnPermsChanged;
    }

    public override void Closed()
    {
        base.Closed();
        _adminMan.OnPermsChanged -= OnPermsChanged;
    }

    private void OnPermsChanged(AdminPermsChangedEventArgs args)
    {
        if (args.Player != Player)
        {
            return;
        }

        StateDirty();
    }
}

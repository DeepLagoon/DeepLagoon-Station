using Content.Server.Chat.Systems;
using Content.Server.GameTicking;
using Content.Server.Radio.EntitySystems;
using Content.Server.StationEvents.Components;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Profiling;
using Robust.Shared.Timing;

namespace Content.Server.StationEvents.Events;

/// <summary>
/// Shared station-event lifetime pass, called from the existing derived-system update order.
/// This system deliberately does not update itself: whichever station-event system updates
/// first performs the pass, after its own active-event work just as before.
/// </summary>
public sealed class StationEventLifecycleSystem : EntitySystem
{
    [Dependency] private GameTicker _gameTicker = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private RadioSystem _radio = default!;
    [Dependency] private MapSystem _maps = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private ProfManager _prof = default!;

    private GameTick? _lastProcessedTick;
    private bool _processing;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent args)
    {
        _lastProcessedTick = null;
    }

    /// <summary>
    /// Runs once per tick for every station event, including bare events without a
    /// derived rule component. Mark the tick before callbacks can re-enter this pass.
    /// Nullable/equality-only guarding also handles the initial tick and tick wraparound.
    /// </summary>
    public void ProcessEvents()
    {
        if (_processing || _lastProcessedTick == _timing.CurTick)
            return;

        _lastProcessedTick = _timing.CurTick;
        _processing = true;
        try
        {
            if (_prof.IsEnabled)
            {
                using var group = _prof.Group("Station event lifecycle");
                ProcessEventsCore();
            }
            else
            {
                ProcessEventsCore();
            }
        }
        finally
        {
            _processing = false;
        }
    }

    private void ProcessEventsCore()
    {
        var query = EntityQueryEnumerator<StationEventComponent, GameRuleComponent>();
        while (query.MoveNext(out var uid, out var stationEvent, out var ruleData))
        {
            if (!_gameTicker.IsGameRuleAdded(uid, ruleData))
                continue;

            if (!_gameTicker.IsGameRuleActive(uid, ruleData) && !HasComp<DelayedStartRuleComponent>(uid))
            {
                _gameTicker.StartGameRule(uid, ruleData);
            }
            else if (stationEvent.EndTime != null && _timing.CurTime >= stationEvent.EndTime && _gameTicker.IsGameRuleActive(uid, ruleData))
            {
                _gameTicker.EndGameRule(uid, ruleData);
            }
            // Frontier: Added Warning for events ending soon
            else if (!stationEvent.WarningAnnounced && stationEvent.EndTime != null && (stationEvent.EndTime.Value - _timing.CurTime).TotalSeconds <= stationEvent.WarningDurationLeft && _gameTicker.IsGameRuleActive(uid, ruleData))
            {
                Filter allPlayersInGame = Filter.Empty().AddWhere(_gameTicker.UserHasJoinedGame); // we don't want to send to players who aren't in game (i.e. in the lobby)
                if (stationEvent.WarningAnnouncement != null)
                    _chat.DispatchFilteredAnnouncement(allPlayersInGame, Loc.GetString(stationEvent.WarningAnnouncement), sender: stationEvent.AnnounceSender is { } send ? Loc.GetString(send) : null, playSound: false, colorOverride: stationEvent.WarningAnnouncementColor);
                if (stationEvent.WarningRadioAnnouncement != null)
                {
                    var message = Loc.GetString(stationEvent.WarningRadioAnnouncement);
                    var mapUid = _maps.GetMap(_gameTicker.DefaultMap); // Hack: need a reference to a valid entity on the default map - the map itself works.
                    _radio.SendRadioMessage(uid, message, stationEvent.WarningRadioAnnouncementChannel, mapUid, escapeMarkup: false);
                }
                _audio.PlayGlobal(stationEvent.WarningAudio, allPlayersInGame, true);
                stationEvent.WarningAnnounced = true;
            }
            // End Frontier
        }
    }

}

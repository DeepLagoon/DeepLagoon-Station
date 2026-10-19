using System.Linq;
using Content.Client.GameTicking.Managers;
using Content.Client.Lobby;
using Content.Shared.Audio.Events;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Robust.Client;
using Robust.Client.ResourceManagement;
using Robust.Client.State;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client.Audio;

// Part of ContentAudioSystem that is responsible for lobby music playing/stopping and round-end sound-effect.
public sealed partial class ContentAudioSystem
{
    [Dependency] private IBaseClient _client = default!;
    [Dependency] private ClientGameTicker _gameTicker = default!;
    [Dependency] private IResourceCache _resourceCache = default!;

    private readonly AudioParams _lobbySoundtrackParams = new(-5f, 1, 0, 0, 0, false, 0f);
    private readonly AudioParams _roundEndSoundEffectParams = new(-5f, 1, 0, 0, 0, false, 0f);

    /// <summary>
    /// EntityUid of lobby restart sound component.
    /// </summary>
    private EntityUid? _lobbyRoundRestartAudioStream;

    /// <summary>
    /// Shuffled list of soundtrack file-names.
    /// </summary>
    private string[]? _lobbyPlaylist;
    private int _lobbyTrackIndex;
    private bool _lobbyMusicStopped;
    private TimeSpan? _lobbyPauseStarted;

    public string? CurrentLobbyTrack => _lobbySoundtrackInfo?.Filename;
    public bool IsLobbyMusicPaused => _lobbyPauseStarted != null;
    public bool CanPauseLobbyMusic => _lobbySoundtrackInfo != null && !_lobbyMusicStopped
        && _state.CurrentState is LobbyState && _configManager.GetCVar(CCVars.LobbyMusicEnabled);
    public bool CanSwitchLobbyTrack => _lobbyPlaylist is { Length: > 1 }
        && !_lobbyMusicStopped && _state.CurrentState is LobbyState
        && _configManager.GetCVar(CCVars.LobbyMusicEnabled);

    /// <summary>
    /// Short info about lobby soundtrack currently playing. Is null if soundtrack is not playing.
    /// </summary>
    private LobbySoundtrackInfo? _lobbySoundtrackInfo;

    private Action<LobbySoundtrackChangedEvent>? _lobbySoundtrackChanged;

    /// <summary>
    /// Event for subscription on lobby soundtrack changes.
    /// </summary>
    public event Action<LobbySoundtrackChangedEvent>? LobbySoundtrackChanged
    {
        add
        {
            if (value != null)
            {
                value(new LobbySoundtrackChangedEvent(CurrentLobbyTrack));

                _lobbySoundtrackChanged += value;
            }
        }
        remove => _lobbySoundtrackChanged -= value;
    }

    /// <summary>
    /// Initializes subscriptions that are related to lobby music.
    /// </summary>
    private void InitializeLobbyMusic()
    {
        Subs.CVar(_configManager, CCVars.LobbyMusicEnabled, LobbyMusicCVarChanged);
        Subs.CVar(_configManager, CCVars.LobbyMusicVolume, LobbyMusicVolumeCVarChanged);

        _state.OnStateChanged += StateManagerOnStateChanged;

        _client.PlayerLeaveServer += OnLeave;

        SubscribeNetworkEvent<LobbyMusicStopEvent>(OnLobbySongStopped);
        SubscribeNetworkEvent<LobbyPlaylistChangedEvent>(OnLobbySongChanged);
    }

    private void OnLobbySongStopped(LobbyMusicStopEvent ev)
    {
        _lobbyMusicStopped = true;
        EndLobbyMusic();
    }

    private void StateManagerOnStateChanged(StateChangedEventArgs args)
    {
        switch (args.NewState)
        {
            case LobbyState:
                StartLobbyMusic();
                break;
            default:
                EndLobbyMusic();
                break;
        }
    }

    private void OnLeave(object? sender, PlayerEventArgs args)
    {
        _lobbyPlaylist = null;
        _lobbyTrackIndex = 0;
        _lobbyMusicStopped = false;
        EndLobbyMusic();
    }

    private void LobbyMusicVolumeCVarChanged(float volume)
    {
        if (_lobbySoundtrackInfo != null)
        {
            _audio.SetVolume(
                _lobbySoundtrackInfo.MusicStreamEntityUid,
                _lobbySoundtrackParams.Volume + SharedAudioSystem.GainToVolume(_configManager.GetCVar(CCVars.LobbyMusicVolume))
            );
        }
    }

    private void LobbyMusicCVarChanged(bool musicEnabled)
    {
        if (musicEnabled && _state.CurrentState is LobbyState)
        {
            StartLobbyMusic();
        }
        else
        {
            EndLobbyMusic();
        }
    }

    private void OnLobbySongChanged(LobbyPlaylistChangedEvent playlistChangedEvent)
    {
        var playlist = playlistChangedEvent.Playlist;
        // A repeated server playlist must not undo this client's selection.
        if (!_lobbyMusicStopped && _lobbyPlaylist != null && _lobbyPlaylist.SequenceEqual(playlist))
        {
            StartLobbyMusic();
            return;
        }

        EndLobbyMusic();
        _lobbyPlaylist = playlist.ToArray();
        _lobbyTrackIndex = 0;
        _lobbyMusicStopped = false;
        StartLobbyMusic();
        _lobbySoundtrackChanged?.Invoke(new LobbySoundtrackChangedEvent(CurrentLobbyTrack));
    }

    /// <summary>
    /// Re-starts playing lobby music from playlist, last sent from server. if there is currently none - does nothing.
    /// </summary>
    private void StartLobbyMusic()
    {
        if (_lobbyPlaylist == null || _lobbyPlaylist.Length == 0 || _lobbyMusicStopped
            || _state.CurrentState is not LobbyState || _lobbySoundtrackInfo != null
            || !_configManager.GetCVar(CCVars.LobbyMusicEnabled))
        {
            return;
        }

        for (var attempt = 0; attempt < _lobbyPlaylist.Length; attempt++)
        {
            var index = (_lobbyTrackIndex + attempt) % _lobbyPlaylist.Length;
            if (!PlaySoundtrack(_lobbyPlaylist[index]))
                continue;
            _lobbyTrackIndex = index;
            break;
        }
    }

    /// <summary>Changes only this client's soundtrack; no request is sent to the server.</summary>
    public bool SwitchLobbyTrack(int direction)
    {
        if (!CanSwitchLobbyTrack || direction == 0)
            return false;

        var playlist = _lobbyPlaylist!;
        // Skip missing/unplayable files with a bounded search instead of leaving the player stuck.
        var index = _lobbyTrackIndex;
        for (var attempt = 0; attempt < playlist.Length; attempt++)
        {
            index = (index + Math.Sign(direction) + playlist.Length) % playlist.Length;
            if (!PlaySoundtrack(playlist[index]))
                continue;
            _lobbyTrackIndex = index;
            return true;
        }
        return false;
    }

    /// <summary>Pauses/resumes the existing local source without replacing it or seeking to zero.</summary>
    public bool ToggleLobbyMusicPaused()
    {
        if (!CanPauseLobbyMusic || _lobbySoundtrackInfo is not { } info
            || !TryComp<AudioComponent>(info.MusicStreamEntityUid, out var component))
            return false;

        if (_lobbyPauseStarted is { } pausedAt)
        {
            _audio.SetState(info.MusicStreamEntityUid, AudioState.Playing, component: component);
            // Playlist timing must exclude the entire pause, just like the audio source does.
            _lobbySoundtrackInfo = info with { NextTrackOn = info.NextTrackOn + _timing.CurTime - pausedAt };
            _lobbyPauseStarted = null;
        }
        else
        {
            _audio.SetState(info.MusicStreamEntityUid, AudioState.Paused, component: component);
            _lobbyPauseStarted = _timing.CurTime;
        }
        _lobbySoundtrackChanged?.Invoke(new LobbySoundtrackChangedEvent(CurrentLobbyTrack));
        return true;
    }

    private bool PlaySoundtrack(string soundtrackFilename)
    {
        if (!_resourceCache.TryGetResource(new ResPath(soundtrackFilename), out AudioResource? audio))
        {
            return false;
        }

        var volume = _lobbySoundtrackParams.Volume + SharedAudioSystem.GainToVolume(_configManager.GetCVar(CCVars.LobbyMusicVolume));
        var playResult = _audio.PlayGlobal(
            soundtrackFilename,
            Filter.Local(),
            false,
            // Prepare the replacement silently so even source creation cannot overlap the old track.
            _lobbySoundtrackParams.WithVolume(float.NegativeInfinity)
        );
        if (playResult == null)
        {
            _sawmill.Warning(
                $"Tried to play lobby soundtrack '{{Filename}}' using {nameof(SharedAudioSystem)}.{nameof(SharedAudioSystem.PlayGlobal)} but it returned default value of EntityUid!",
                soundtrackFilename);
            return false;
        }

        // Stop the previous stream only after its replacement has successfully started.
        if (_lobbySoundtrackInfo is { } previous)
            StopLobbyStream(previous.MusicStreamEntityUid);
        var nextTrackOn = _timing.CurTime + audio.AudioStream.Length;
        _lobbySoundtrackInfo = new LobbySoundtrackInfo(soundtrackFilename, nextTrackOn, playResult.Value.Entity);
        if (IsLobbyMusicPaused)
        {
            // Choosing a different track must not unexpectedly resume music for a paused listener.
            _audio.SetState(playResult.Value.Entity, AudioState.Paused);
            _lobbyPauseStarted = _timing.CurTime;
        }
        _audio.SetVolume(playResult.Value.Entity, volume);

        var lobbySongChangedEvent = new LobbySoundtrackChangedEvent(soundtrackFilename);
        _lobbySoundtrackChanged?.Invoke(lobbySongChangedEvent);
        return true;
    }

    private void StopLobbyStream(EntityUid uid)
    {
        if (TryComp<AudioComponent>(uid, out var component))
        {
            // Stop() only queues entity deletion. During a server stall simulation ticks may
            // stop, while UI input and audio rendering continue. Silence the source now and
            // prevent its first FrameUpdate from starting it again before deletion runs.
            _audio.SetGain(uid, 0f, component);
            _audio.SetState(uid, AudioState.Stopped, component: component);
            component.Started = true;
        }
        // These streams are client-only; cleanup must not be skipped by Stop's prediction guard.
        QueueDel(uid);
    }

    private void EndLobbyMusic()
    {
        if (_lobbySoundtrackInfo != null)
            StopLobbyStream(_lobbySoundtrackInfo.MusicStreamEntityUid);
        _lobbySoundtrackInfo = null;
        _lobbyPauseStarted = null;
        var lobbySongChangedEvent = new LobbySoundtrackChangedEvent();
        _lobbySoundtrackChanged?.Invoke(lobbySongChangedEvent);
    }

    private void PlayRestartSound(RoundRestartCleanupEvent ev)
    {
        if (!_configManager.GetCVar(CCVars.RestartSoundsEnabled))
            return;

        var file = _gameTicker.RestartSound;
        if (ResolvedSoundSpecifier.IsNullOrEmpty(file))
        {
            return;
        }

        _lobbyRoundRestartAudioStream = _audio.PlayGlobal(
            file,
            Filter.Local(),
            false,
            _roundEndSoundEffectParams.WithVolume(_roundEndSoundEffectParams.Volume + SharedAudioSystem.GainToVolume(_configManager.GetCVar(CCVars.LobbyMusicVolume)))
        )?.Entity;
    }

    private void ShutdownLobbyMusic()
    {
        _state.OnStateChanged -= StateManagerOnStateChanged;

        _client.PlayerLeaveServer -= OnLeave;

        EndLobbyMusic();
    }

    private void UpdateLobbyMusic()
    {
        if (
            _lobbySoundtrackInfo != null
            && !IsLobbyMusicPaused
            && _timing.CurTime >= _lobbySoundtrackInfo.NextTrackOn
            && _lobbyPlaylist?.Length > 0
            )
        {
            if (_lobbyPlaylist.Length == 1)
            {
                if (!PlaySoundtrack(_lobbyPlaylist[0]))
                    EndLobbyMusic();
            }
            else if (!SwitchLobbyTrack(1))
                EndLobbyMusic();
        }
    }

    /// <summary> Container for lobby soundtrack information. </summary>
    /// <param name="Filename">Soundtrack filename.</param>
    /// <param name="NextTrackOn">Time (based on <see cref="IGameTiming.CurTime"/>) when this track is going to finish playing and next track have to be started.</param>
    /// <param name="MusicStreamEntityUid">
    /// EntityUid of launched soundtrack (from <see cref="SharedAudioSystem.PlayGlobal(string,Robust.Shared.Player.Filter,bool,System.Nullable{Robust.Shared.Audio.AudioParams})"/>).
    /// </param>
    private sealed record LobbySoundtrackInfo(string Filename, TimeSpan NextTrackOn, EntityUid MusicStreamEntityUid);
}

/// <summary>
/// Event of changing lobby soundtrack (or stopping lobby music - will pass null for <paramref name="SoundtrackFilename"/> in that case).
/// Is used by <see cref="ContentAudioSystem.LobbySoundtrackChanged"/> and <see cref="LobbyState.UpdateLobbySoundtrackInfo"/>.
/// </summary>
/// <param name="SoundtrackFilename">Filename of newly set soundtrack, or null if soundtrack playback is stopped.</param>
public sealed record LobbySoundtrackChangedEvent(string? SoundtrackFilename = null);

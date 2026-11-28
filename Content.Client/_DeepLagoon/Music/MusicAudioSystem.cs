using System.Collections.Generic;
using System.IO;
using Content.Shared._DeepLagoon.Music;
using Robust.Client.Audio;
using Robust.Client.ResourceManagement;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Music;

public sealed class MusicAudioSystem : EntitySystem
{
    [Dependency] private readonly IAudioManager _audioManager = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly ILogManager _logManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IEntityManager _entities = default!;

    private ISawmill _sawmill = default!;

    private readonly Dictionary<string, ChannelState> _channels = new();

    private sealed class ChannelState
    {
        public readonly string Name;
        public readonly Dictionary<string, MemoryStream> Pending = new();

        public AudioStream? Stream;
        public AudioStream? StartedFor;
        public EntityUid? Playing;
        public string? TrackId;

        public uint SourceNetId;
        public float MaxDistance = MusicConstants.SpeakerMaxDistance;

        public float StartAt = -1f;
        public float StartDelay;
        public float Offset;
        public float Gain = 0.75f;

        public bool PlayRequested;
        public float RequestedPosition;
        public bool PositionLocked;

        public MusicPlaybackStatus Status = MusicPlaybackStatus.Stopped;

        public ChannelState(string name)
        {
            Name = name;
        }
    }

    private ChannelState Channel(string name)
    {
        if (_channels.TryGetValue(name, out var existing))
            return existing;

        var created = new ChannelState(name);
        _channels[name] = created;
        return created;
    }

    public float GainOf(string channel) => Channel(channel).Gain;

    public MusicPlaybackStatus StatusOf(string channel) => Channel(channel).Status;

    public string? TrackOf(string channel) => Channel(channel).TrackId;

    public float PositionOf(string channel)
    {
        var state = Channel(channel);

        if (state.PositionLocked)
            return state.Offset;

        return state.Playing != null && TryComp<AudioComponent>(state.Playing, out var audio)
            ? (float) ((audio.PauseTime ?? _timing.CurTime) - audio.AudioStart).TotalSeconds
            : state.Offset;
    }

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = _logManager.GetSawmill("dl.music.client");
    }

    public void Prepare(string channel, string trackId, uint sourceNetId, float maxDistance, float startDelay,
        float gain)
    {
        var state = Channel(channel);

        state.TrackId = trackId;
        state.SourceNetId = sourceNetId;
        state.MaxDistance = float.IsFinite(maxDistance) && maxDistance > 0f
            ? maxDistance
            : MusicConstants.SpeakerMaxDistance;
        state.StartDelay = float.IsFinite(startDelay) && startDelay > 0f ? startDelay : 0f;

        if (float.IsFinite(gain))
            state.Gain = Math.Clamp(gain, 0f, 1f);
    }

    public void Begin(string channel, string trackId)
    {
        var state = Channel(channel);

        if (state.TrackId != null && state.TrackId != trackId)
            return;

        DiscardPending(state, trackId);
        state.TrackId = trackId;
        state.Pending[trackId] = new MemoryStream();
        state.Status = MusicPlaybackStatus.Downloading;
    }

    public void Append(string channel, string trackId, byte[] data, int length)
    {
        var state = Channel(channel);
        if (!state.Pending.TryGetValue(trackId, out var buffer))
            return;

        buffer.Write(data, 0, length);
    }

    public bool Complete(string channel, string trackId)
    {
        var state = Channel(channel);

        if (!state.Pending.Remove(trackId, out var buffer))
            return false;

        byte[] bytes;
        using (buffer)
        {
            bytes = buffer.ToArray();
        }

        if (state.TrackId != trackId)
        {
            state.Status = MusicPlaybackStatus.Stopped;
            return false;
        }

        AudioStream? stream = null;
        try
        {
            using var streamIn = new MemoryStream(bytes, false);
            stream = _audioManager.LoadAudioWav(streamIn, $"{channel}:{trackId}");
        }
        catch (Exception e)
        {
            _sawmill.Warning("Music decode failed on {Channel}: {Error}", channel, e.GetType().Name);
        }

        if (stream == null)
        {
            state.Status = MusicPlaybackStatus.Stopped;
            return false;
        }

        if (state.Playing != null)
            _audio.Stop(state.Playing);

        state.Playing = null;

        state.Stream?.Dispose();
        state.Stream = stream;
        state.StartedFor = null;
        state.PositionLocked = false;

        if (state.Status != MusicPlaybackStatus.Paused)
        {
            state.StartAt = (float) _timing.CurTime.TotalSeconds + state.StartDelay;
            state.Status = MusicPlaybackStatus.Downloading;
        }

        state.StartDelay = 0f;
        return true;
    }

    public void Play(string channel, float position)
    {
        var state = Channel(channel);
        state.PlayRequested = true;
        state.RequestedPosition = float.IsFinite(position) && position > 0f ? position : 0f;

        if (state.Stream == null)
            return;

        if (state.StartAt > 0f && (float) _timing.CurTime.TotalSeconds < state.StartAt)
            return;

        if (state.Status == MusicPlaybackStatus.Paused)
            state.Status = MusicPlaybackStatus.Downloading;

        Start(state);
    }

    private void Start(ChannelState state)
    {
        if (state.Stream == null)
            return;

        if (state.Status == MusicPlaybackStatus.Paused)
            return;

        if (state.Playing != null && TryComp<AudioComponent>(state.Playing, out var existing))
        {
            if (existing.State == AudioState.Paused)
            {
                _audio.SetState(state.Playing, AudioState.Playing);
                ApplyGain(state);
                state.Status = MusicPlaybackStatus.Playing;
                state.StartAt = -1f;
                return;
            }

            if (existing.State == AudioState.Playing)
            {
                state.Status = MusicPlaybackStatus.Playing;
                state.StartAt = -1f;
                return;
            }
        }

        if (state.StartedFor == state.Stream && state.Playing != null && !TerminatingOrDeleted(state.Playing.Value))
        {
            state.Status = MusicPlaybackStatus.Playing;
            state.StartAt = -1f;
            return;
        }

        EntityUid? source = null;
        if (state.SourceNetId != 0)
        {
            if (!TryResolveSource(state.SourceNetId, out var resolved))
            {
                _sawmill.Debug("Music on {Channel} held: source {Source} not available yet",
                    state.Name, state.SourceNetId);
                return;
            }

            source = resolved;
        }

        var audioParams = AudioParams.Default.WithMaxDistance(state.MaxDistance);
        state.Offset = state.RequestedPosition;
        if (state.Offset > 0f)
            audioParams = audioParams.WithPlayOffset(state.Offset);

        (EntityUid Entity, AudioComponent Component)? played;
        if (source != null)
            played = _audio.PlayEntity(state.Stream, source.Value, null, audioParams);
        else
            played = _audio.PlayGlobal(state.Stream, null, audioParams);

        if (played == null)
        {
            state.Status = MusicPlaybackStatus.Stopped;
            return;
        }

        state.Playing = played.Value.Entity;
        state.StartedFor = state.Stream;
        state.StartAt = -1f;
        ApplyGain(state);
        state.Status = MusicPlaybackStatus.Playing;
    }

    public void Seek(string channel, float position)
    {
        if (!float.IsFinite(position))
            return;

        var state = Channel(channel);
        state.RequestedPosition = Math.Max(0f, position);
        state.PlayRequested = true;

        if (state.Stream == null)
            return;

        if (state.Playing != null && TryComp<AudioComponent>(state.Playing, out var audio))
        {
            audio.PlaybackPosition = state.RequestedPosition;
            state.Offset = state.RequestedPosition;
            state.PositionLocked = true;
            state.Status = audio.State == AudioState.Paused
                ? MusicPlaybackStatus.Paused
                : MusicPlaybackStatus.Playing;
            return;
        }

        Start(state);
    }

    public void Pause(string channel)
    {
        var state = Channel(channel);

        if (state.Playing != null)
        {
            state.Offset = PositionOf(channel);
            state.PositionLocked = true;
            _audio.SetState(state.Playing, AudioState.Paused);
        }

        state.StartAt = -1f;
        state.PlayRequested = false;
        state.Status = MusicPlaybackStatus.Paused;
    }

    public void SetGain(string channel, float gain)
    {
        if (!float.IsFinite(gain))
            return;

        var state = Channel(channel);
        state.Gain = Math.Clamp(gain, 0f, 1f);
        ApplyGain(state);
    }

    private bool TryResolveSource(uint netId, out EntityUid source)
    {
        source = EntityUid.Invalid;

        var netEntity = new NetEntity((int) netId);
        if (!_entities.TryGetEntity(netEntity, out var found))
            return false;

        source = found.Value;
        return true;
    }

    private void ApplyGain(ChannelState state)
    {
        if (state.Playing != null)
            _audio.SetGain(state.Playing, state.Gain);
    }

    private static void DiscardPending(ChannelState state, string trackId)
    {
        if (!state.Pending.Remove(trackId, out var stale))
            return;

        stale.Dispose();
    }

    public void Finish(string channel)
    {
        var state = Channel(channel);

        foreach (var buffer in state.Pending.Values)
            buffer.Dispose();
        state.Pending.Clear();

        if (state.Playing != null)
            _audio.Stop(state.Playing);

        state.Playing = null;
        state.Stream?.Dispose();
        state.Stream = null;
        state.StartedFor = null;
        state.TrackId = null;
        state.SourceNetId = 0;
        state.Offset = 0f;
        state.StartAt = -1f;
        state.StartDelay = 0f;
        state.PlayRequested = false;
        state.RequestedPosition = 0f;
        state.PositionLocked = false;
        state.Status = MusicPlaybackStatus.Stopped;
    }

    public void FinishAll()
    {
        foreach (var name in _channels.Keys)
            Finish(name);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        foreach (var state in _channels.Values)
        {
            if (state.Status == MusicPlaybackStatus.Playing && state.Playing != null &&
                TryComp<AudioComponent>(state.Playing, out var audio) && audio.State == AudioState.Stopped)
            {
                Finish(state.Name);
                continue;
            }

            if (state.Status != MusicPlaybackStatus.Paused && state.PlayRequested && state.Stream != null &&
                state.StartAt > 0f && (float) _timing.CurTime.TotalSeconds >= state.StartAt)
            {
                Start(state);
                continue;
            }

            if (state.Status == MusicPlaybackStatus.Downloading && state.PlayRequested && state.Stream != null &&
                state.StartAt < 0f)
            {
                Start(state);
            }
        }
    }
}

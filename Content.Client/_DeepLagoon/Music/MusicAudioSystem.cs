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

    private readonly Dictionary<string, MemoryStream> _pending = new();

    private AudioStream? _stream;
    private AudioStream? _startedFor;
    private EntityUid? _playing;
    private string? _trackId;

    private uint _sourceNetId;
    private float _maxDistance = MusicConstants.SpeakerMaxDistance;

    private float _startAt = -1f;
    private float _startDelay;
    private float _offset;
    private float _gain = 0.75f;

    private bool _playRequested;
    private float _requestedPosition;

    private bool _positionLocked;

    public float Gain => _gain;

    public MusicPlaybackStatus Status { get; private set; } = MusicPlaybackStatus.Stopped;

    public string? CurrentTrackId => _trackId;

    public float Position
    {
        get
        {
            if (_positionLocked)
                return _offset;

            return _playing != null && TryComp<AudioComponent>(_playing, out var audio)
                ? (float) ((audio.PauseTime ?? _timing.CurTime) - audio.AudioStart).TotalSeconds
                : _offset;
        }
    }

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = _logManager.GetSawmill("dl.music.client");
    }

    public void Prepare(string trackId, uint sourceNetId, float maxDistance, float startDelay, float gain)
    {
        _trackId = trackId;
        _sourceNetId = sourceNetId;
        _maxDistance = float.IsFinite(maxDistance) && maxDistance > 0f
            ? maxDistance
            : MusicConstants.SpeakerMaxDistance;
        _startDelay = float.IsFinite(startDelay) && startDelay > 0f ? startDelay : 0f;

        if (float.IsFinite(gain))
            _gain = Math.Clamp(gain, 0f, 1f);
    }

    public void Begin(string trackId)
    {
        if (_trackId != null && _trackId != trackId)
            return;

        DiscardPending(trackId);
        _trackId = trackId;
        _pending[trackId] = new MemoryStream();
        Status = MusicPlaybackStatus.Downloading;
    }

    public void Append(string trackId, byte[] data, int length)
    {
        if (!_pending.TryGetValue(trackId, out var buffer))
            return;

        buffer.Write(data, 0, length);
    }

    public bool Complete(string trackId)
    {
        if (!_pending.Remove(trackId, out var buffer))
            return false;

        byte[] bytes;
        using (buffer)
        {
            bytes = buffer.ToArray();
        }

        if (_trackId != trackId)
        {
            Status = MusicPlaybackStatus.Stopped;
            return false;
        }

        AudioStream? stream = null;
        try
        {
            using var streamIn = new MemoryStream(bytes, false);
            stream = _audioManager.LoadAudioWav(streamIn, trackId);
        }
        catch (Exception e)
        {
            _sawmill.Warning("Music decode failed: {Error}", e.GetType().Name);
        }

        if (stream == null)
        {
            Status = MusicPlaybackStatus.Stopped;
            return false;
        }

        if (_playing != null)
            _audio.Stop(_playing);

        _playing = null;

        _stream?.Dispose();
        _stream = stream;
        _startedFor = null;
        _positionLocked = false;

        if (Status != MusicPlaybackStatus.Paused)
        {
            _startAt = (float) _timing.CurTime.TotalSeconds + _startDelay;
            Status = MusicPlaybackStatus.Downloading;
        }

        _startDelay = 0f;
        return true;
    }

    public void Play(float position)
    {
        _playRequested = true;
        _requestedPosition = float.IsFinite(position) && position > 0f ? position : 0f;

        if (_stream == null)
            return;

        if (_startAt > 0f && (float) _timing.CurTime.TotalSeconds < _startAt)
            return;

        if (Status == MusicPlaybackStatus.Paused)
            Status = MusicPlaybackStatus.Downloading;

        Start();
    }

    private void Start()
    {
        if (_stream == null)
            return;

        if (Status == MusicPlaybackStatus.Paused)
            return;

        if (_playing != null && TryComp<AudioComponent>(_playing, out var existing))
        {
            if (existing.State == AudioState.Paused)
            {
                _audio.SetState(_playing, AudioState.Playing);
                ApplyGain();
                Status = MusicPlaybackStatus.Playing;
                _startAt = -1f;
                return;
            }

            if (existing.State == AudioState.Playing)
            {
                Status = MusicPlaybackStatus.Playing;
                _startAt = -1f;
                return;
            }
        }

        if (_startedFor == _stream && _playing != null && !TerminatingOrDeleted(_playing.Value))
        {
            Status = MusicPlaybackStatus.Playing;
            _startAt = -1f;
            return;
        }

        EntityUid? source = null;
        if (_sourceNetId != 0)
        {
            if (!TryResolveSource(_sourceNetId, out var resolved))
            {
                _sawmill.Debug("Music playback held: source entity {Source} is not available yet", _sourceNetId);
                return;
            }

            source = resolved;
        }

        var audioParams = AudioParams.Default.WithMaxDistance(_maxDistance);
        _offset = _requestedPosition;
        if (_offset > 0f)
            audioParams = audioParams.WithPlayOffset(_offset);

        (EntityUid Entity, AudioComponent Component)? played;
        if (source != null)
            played = _audio.PlayEntity(_stream, source.Value, null, audioParams);
        else
            played = _audio.PlayGlobal(_stream, null, audioParams);

        if (played == null)
        {
            Status = MusicPlaybackStatus.Stopped;
            return;
        }

        _playing = played.Value.Entity;
        _startedFor = _stream;
        _startAt = -1f;
        ApplyGain();
        Status = MusicPlaybackStatus.Playing;
    }

    public void Seek(float position)
    {
        if (!float.IsFinite(position))
            return;

        _requestedPosition = Math.Max(0f, position);
        _playRequested = true;

        if (_stream == null)
            return;

        if (_playing != null && TryComp<AudioComponent>(_playing, out var audio))
        {
            audio.PlaybackPosition = _requestedPosition;
            _offset = _requestedPosition;
            _positionLocked = true;
            Status = audio.State == AudioState.Paused ? MusicPlaybackStatus.Paused : MusicPlaybackStatus.Playing;
            return;
        }

        Start();
    }

    public void Pause()
    {
        if (_playing != null)
        {
            _offset = Position;
            _positionLocked = true;
            _audio.SetState(_playing, AudioState.Paused);
        }

        _startAt = -1f;
        _playRequested = false;
        Status = MusicPlaybackStatus.Paused;
    }

    public void SetGain(float gain)
    {
        if (!float.IsFinite(gain))
            return;

        _gain = Math.Clamp(gain, 0f, 1f);
        ApplyGain();
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

    private void ApplyGain()
    {
        if (_playing != null)
            _audio.SetGain(_playing, _gain);
    }

    private void DiscardPending(string trackId)
    {
        if (!_pending.Remove(trackId, out var stale))
            return;

        stale.Dispose();
    }

    public void Finish()
    {
        foreach (var buffer in _pending.Values)
            buffer.Dispose();
        _pending.Clear();

        if (_playing != null)
            _audio.Stop(_playing);

        _playing = null;
        _stream?.Dispose();
        _stream = null;
        _startedFor = null;
        _trackId = null;
        _sourceNetId = 0;
        _offset = 0f;
        _startAt = -1f;
        _startDelay = 0f;
        _playRequested = false;
        _requestedPosition = 0f;
        _positionLocked = false;
        Status = MusicPlaybackStatus.Stopped;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (Status == MusicPlaybackStatus.Playing && _playing != null &&
            TryComp<AudioComponent>(_playing, out var audio) && audio.State == AudioState.Stopped)
        {
            Finish();
            return;
        }

        if (Status != MusicPlaybackStatus.Paused && _playRequested && _stream != null &&
            _startAt > 0f && (float) _timing.CurTime.TotalSeconds >= _startAt)
            Start();

        if (Status == MusicPlaybackStatus.Downloading && _playRequested && _stream != null && _startAt < 0f)
            Start();
    }
}

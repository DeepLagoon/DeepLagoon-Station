using Content.Shared._DeepLagoon.LustWish;
using Content.Shared._DeepLagoon.Speech;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Robust.Client.Player;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Speech;

public sealed partial class SpeechBarkSystem : EntitySystem
{
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IRobustRandom _random = default!;

    private const int MaxSpeakers = 24;
    private const int MaxOverlappingNotes = 3;
    private const int LateNoteToleranceMs = 225;
    private readonly Dictionary<EntityUid, Playback> _active = new();
    private readonly List<EntityUid> _finished = new();
    private readonly Filter _local = Filter.Local();
    private float _gain;
    private Playback? _preview;

    public override void Initialize()
    {
        SubscribeNetworkEvent<SpeechBarkEvent>(OnSpeech);
        SubscribeNetworkEvent<RoundRestartCleanupEvent>(_ => Clear());
        SubscribeLocalEvent<LocalPlayerDetachedEvent>(_ => Clear());
        Subs.CVar(_cfg, CCVars.SpeechBarkVolume, OnVolumeChanged, true);
    }

    public override void Shutdown()
    {
        Clear();
        base.Shutdown();
    }

    private void OnSpeech(SpeechBarkEvent ev)
    {
        if (!TryGetEntity(ev.Source, out var uid))
            return;

        var source = uid.Value;
        if (_active.Remove(source, out var old))
            Stop(old);

        if (_gain <= 0 || ev.Offsets.Length == 0 || ev.Pitches.Length != ev.Offsets.Length ||
            _active.Count >= MaxSpeakers || !CanHear(source, ev.Range) ||
            !_prototypes.TryIndex(ev.Voice, out var voice))
            return;

        _active.Add(source, new Playback(ev, voice.Sound, _timing.RealTime));
    }

    public void Preview(SpeechBarkSettings settings, ProtoId<SpeechBarkVoicePrototype>? fallback)
    {
        StopPreview();
        var voice = settings.ResolveVoice(_prototypes, fallback);
        settings = settings.ForVoice(voice);
        var offsets = SpeechBarkRhythm.Build(Loc.GetString("speech-bark-preview-phrase"), settings);
        if (offsets.Length == 0)
            return;

        var ev = new SpeechBarkEvent(NetEntity.Invalid, offsets, settings.RollPitches(offsets.Length, _random),
            voice.ID, 0, false);
        _preview = new Playback(ev, voice.Sound, _timing.RealTime);
    }

    public void StopPreview()
    {
        if (_preview != null)
            Stop(_preview);
        _preview = null;
    }

    public override void FrameUpdate(float frameTime)
    {
        if (_active.Count == 0 && _preview == null)
            return;

        var now = _timing.RealTime;
        if (_preview != null && !Advance(_preview, null, now))
            StopPreview();

        foreach (var (source, playback) in _active)
        {
            if (CanHear(source, playback.Event.Range) && Advance(playback, source, now))
                continue;

            Stop(playback);
            _finished.Add(source);
        }

        foreach (var source in _finished)
            _active.Remove(source);
        _finished.Clear();
    }

    private bool Advance(Playback playback, EntityUid? source, TimeSpan now)
    {
        var ev = playback.Event;
        var offsets = ev.Offsets;
        var index = playback.NextNote;
        for (var i = playback.Audio.Count - 1; i >= 0; i--)
        {
            if (!HasComp<AudioComponent>(playback.Audio[i]))
                playback.Audio.RemoveAt(i);
        }

        if (index == offsets.Length)
            return playback.Audio.Count > 0;

        var elapsed = (now - playback.Start).TotalMilliseconds;
        if (elapsed < offsets[index])
            return true;

        if (elapsed > offsets[^1] + LateNoteToleranceMs)
        {
            playback.NextNote = offsets.Length;
            return playback.Audio.Count > 0;
        }

        // Play only the current beat after a stalled frame.
        while (index + 1 < offsets.Length && elapsed >= offsets[index + 1])
            index++;

        // The imported samples have different lengths. Let notes overlap within a per-speaker budget.
        if (playback.Audio.Count >= MaxOverlappingNotes)
        {
            _audio.Stop(playback.Audio[0]);
            playback.Audio.RemoveAt(0);
        }

        var parameters = AudioParams.Default.WithPitchScale(ev.Pitches[index]);
        var audio = source is { } speaker
            ? _audio.PlayEntity(playback.Sound, _local, speaker, false, parameters
                .WithMaxDistance(ev.Range)
                .WithReferenceDistance(ev.Whisper ? 1f : 1.5f)
                .WithRolloffFactor(2f)
                .WithVolume(GetVolume(ev)))?.Entity
            : _audio.PlayGlobal(playback.Sound, _local, false, parameters.WithVolume(-10f))?.Entity;
        if (audio is { } uid)
            playback.Audio.Add(uid);

        playback.NextNote = index + 1;
        return true;
    }

    private bool CanHear(EntityUid source, float range)
    {
        if (TerminatingOrDeleted(source) || _player.LocalEntity is not { } listener)
            return false;

        if (listener != source && TryComp<LustWishWornEffectsComponent>(listener, out var sensory) && sensory.Deaf)
            return false;

        return _transform.InRange(source, listener, range);
    }

    private float GetVolume(SpeechBarkEvent ev) =>
        (ev.Whisper ? -14f : -6f) + SharedAudioSystem.GainToVolume(_gain);

    private void OnVolumeChanged(float gain)
    {
        _gain = float.IsFinite(gain) ? Math.Clamp(gain, 0f, 1f) : 0f;
        if (_gain <= 0)
        {
            ClearSpeech();
            return;
        }

        foreach (var playback in _active.Values)
        {
            foreach (var audio in playback.Audio)
            {
                if (HasComp<AudioComponent>(audio))
                    _audio.SetVolume(audio, GetVolume(playback.Event));
            }
        }
    }

    private void Clear()
    {
        StopPreview();
        ClearSpeech();
    }

    private void ClearSpeech()
    {
        foreach (var playback in _active.Values)
            Stop(playback);

        _active.Clear();
    }

    private void Stop(Playback playback)
    {
        foreach (var audio in playback.Audio)
            _audio.Stop(audio);

        playback.Audio.Clear();
    }

    private sealed class Playback(SpeechBarkEvent ev, SoundSpecifier sound, TimeSpan start)
    {
        public readonly SpeechBarkEvent Event = ev;
        public readonly SoundSpecifier Sound = sound;
        public readonly TimeSpan Start = start;
        public readonly List<EntityUid> Audio = new(MaxOverlappingNotes);
        public int NextNote;
    }
}

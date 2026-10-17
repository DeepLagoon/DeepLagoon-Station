using System.Linq;
using Content.Server.Power.EntitySystems;
using Content.Shared.Audio.Jukebox;
using Content.Shared.Power;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.Audio.Jukebox;

public sealed partial class JukeboxSystem : SharedJukeboxSystem
{
    [Dependency] private IPrototypeManager _protoManager = default!;
    [Dependency] private AppearanceSystem _appearanceSystem = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private IGameTiming _timing = default!;
    private readonly Dictionary<EntityUid, EntityUid> _audioOwners = new();
    private readonly HashSet<EntityUid> _active = new();
    private readonly Dictionary<string, float> _lengths = new();
    private float _updateTimer;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<JukeboxComponent, JukeboxSelectedMessage>(OnSelected);
        SubscribeLocalEvent<JukeboxComponent, JukeboxPlayingMessage>((Entity<JukeboxComponent> e, ref JukeboxPlayingMessage _) => Play(e));
        SubscribeLocalEvent<JukeboxComponent, JukeboxPauseMessage>(OnPause);
        SubscribeLocalEvent<JukeboxComponent, JukeboxStopMessage>((Entity<JukeboxComponent> e, ref JukeboxStopMessage _) => Stop(e));
        SubscribeLocalEvent<JukeboxComponent, JukeboxSetTimeMessage>(OnSeek);
        SubscribeLocalEvent<JukeboxComponent, JukeboxSetGainMessage>(OnGain);
        SubscribeLocalEvent<JukeboxComponent, JukeboxQueueMessage>(OnQueue);
        SubscribeLocalEvent<JukeboxComponent, JukeboxLibraryRequest>(OnLibrary);
        SubscribeLocalEvent<JukeboxComponent, BoundUIOpenedEvent>(OnOpened);
        SubscribeLocalEvent<JukeboxComponent, ComponentInit>((Entity<JukeboxComponent> e, ref ComponentInit _) => TryUpdateVisualState(e));
        SubscribeLocalEvent<JukeboxComponent, PowerChangedEvent>(OnPower);
        SubscribeLocalEvent<JukeboxComponent, ComponentShutdown>((Entity<JukeboxComponent> e, ref ComponentShutdown _) => Stop(e));
        SubscribeLocalEvent<EntityTerminatingEvent>(OnTerminating);
        _protoManager.PrototypesReloaded += OnReload;
    }

    public override void Shutdown()
    {
        _protoManager.PrototypesReloaded -= OnReload;
        base.Shutdown();
    }

    private void OnReload(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<JukeboxPrototype>()) _lengths.Clear();
    }

    private float Length(JukeboxPrototype track)
    {
        if (!_lengths.TryGetValue(track.ID, out var length))
            _lengths[track.ID] = length = (float) Audio.GetAudioLength(new ResolvedPathSpecifier(track.Path.Path.ToString())).TotalSeconds;
        return length;
    }

    private void Publish(Entity<JukeboxComponent> ent)
    {
        ent.Comp.QueueRevision++;
        _ui.SetUiState(ent.Owner, JukeboxUiKey.Key,
            new JukeboxState(ent.Comp.Queue.ToArray(), ent.Comp.Repeat, ent.Comp.QueueRevision, this.IsPowered(ent.Owner, EntityManager)));
    }

    private void OnOpened(Entity<JukeboxComponent> ent, ref BoundUIOpenedEvent args) => Publish(ent);

    private void OnGain(Entity<JukeboxComponent> ent, ref JukeboxSetGainMessage args)
    {
        if (!float.IsFinite(args.Gain)) return;
        var gain = Math.Clamp(args.Gain, 0, 1);
        if (Math.Abs(ent.Comp.Gain - gain) < 0.001f) return;
        ent.Comp.Gain = gain;
        Audio.SetGain(ent.Comp.AudioStream, gain);
        Dirty(ent);
    }

    private void Play(Entity<JukeboxComponent> ent)
    {
        if (!this.IsPowered(ent.Owner, EntityManager)) return;
        if (TryComp<AudioComponent>(ent.Comp.AudioStream, out var audio) && audio.State == AudioState.Paused)
        {
            Audio.SetState(ent.Comp.AudioStream, AudioState.Playing);
            _active.Add(ent.Owner);
            return;
        }
        if (audio?.State == AudioState.Playing) return;
        if (ent.Comp.SelectedSongId == null && ent.Comp.Queue.Count > 0)
        {
            ent.Comp.SelectedSongId = ent.Comp.Queue[0].Track;
            ent.Comp.Queue.RemoveAt(0);
            Publish(ent);
        }
        if (!_protoManager.TryIndex(ent.Comp.SelectedSongId, out var track)) return;
        StopAudio(ent);
        // PVS distributes only spatial audio; AudioStart synchronizes new listeners.
        var stream = Audio.PlayPvs(track.Path, ent.Owner,
            AudioParams.Default.WithMaxDistance(10).WithVolume(SharedAudioSystem.GainToVolume(ent.Comp.Gain)));
        if (stream == null) return;
        ent.Comp.AudioStream = stream.Value.Entity;
        _audioOwners[stream.Value.Entity] = ent.Owner;
        Transform(stream.Value.Entity).LocalPosition = ent.Comp.AudioOffset;
        _active.Add(ent.Owner);
        Dirty(ent);
        TryUpdateVisualState(ent);
    }

    private void StopAudio(Entity<JukeboxComponent> ent)
    {
        // Detach before deleting: an intentional Stop must never advance the queue.
        if (ent.Comp.AudioStream is { } stream) _audioOwners.Remove(stream);
        ent.Comp.AudioStream = Audio.Stop(ent.Comp.AudioStream);
        _active.Remove(ent.Owner);
        Dirty(ent);
    }

    private void Stop(Entity<JukeboxComponent> ent)
    {
        StopAudio(ent);
        ent.Comp.Selecting = false;
    }

    private void OnPause(Entity<JukeboxComponent> ent, ref JukeboxPauseMessage args)
    {
        Audio.SetState(ent.Comp.AudioStream, AudioState.Paused);
        _active.Remove(ent.Owner);
    }

    private void OnSeek(Entity<JukeboxComponent> ent, ref JukeboxSetTimeMessage args)
    {
        if (!float.IsFinite(args.SongTime) || !_protoManager.TryIndex(ent.Comp.SelectedSongId, out var track) ||
            !TryComp<AudioComponent>(ent.Comp.AudioStream, out var audio)) return;
        var offset = audio.State == AudioState.Playing && TryComp<ActorComponent>(args.Actor, out var actor)
            ? actor.PlayerSession.Channel.Ping / 1000f : 0;
        Audio.SetPlaybackPosition(ent.Comp.AudioStream, Math.Clamp(args.SongTime + offset, 0, Math.Max(0, Length(track) - 0.01f)));
    }

    private void OnSelected(Entity<JukeboxComponent> ent, ref JukeboxSelectedMessage args)
    {
        if (!_protoManager.HasIndex(args.SongId) || Audio.IsPlaying(ent.Comp.AudioStream)) return;
        StopAudio(ent);
        ent.Comp.SelectedSongId = args.SongId;
        ent.Comp.Selecting = true;
        ent.Comp.SelectAccumulator = 0;
        _active.Add(ent.Owner);
        _appearanceSystem.SetData(ent.Owner, JukeboxVisuals.VisualState, JukeboxVisualState.Select);
        Dirty(ent);
    }

    private void OnQueue(Entity<JukeboxComponent> ent, ref JukeboxQueueMessage args)
    {
        if (args.Id == null || args.Id.Length > 128) return;
        switch (args.Action)
        {
            case JukeboxAction.Add:
            case JukeboxAction.AddFirst:
                if (!AddTracks(ent, new[] { args.Id }, args.Action == JukeboxAction.AddFirst)) return;
                break;
            case JukeboxAction.PlayTrack:
                if (!_protoManager.HasIndex<JukeboxPrototype>(args.Id)) return;
                StopAudio(ent); ent.Comp.SelectedSongId = args.Id; Play(ent); break;
            case JukeboxAction.Move:
            case JukeboxAction.Remove:
                if (args.Revision != ent.Comp.QueueRevision) { Publish(ent); return; }
                var entryId = args.Id;
                var index = ent.Comp.Queue.FindIndex(t => t.Id == entryId);
                if (index < 0) return;
                if (args.Action == JukeboxAction.Remove) ent.Comp.Queue.RemoveAt(index);
                else if (!JukeboxRules.Move(ent.Comp.Queue, index, args.Index)) return;
                break;
            case JukeboxAction.Clear:
                if (args.Revision != ent.Comp.QueueRevision) { Publish(ent); return; }
                ent.Comp.Queue.Clear(); break;
            case JukeboxAction.Next: Advance(ent, true); return;
            case JukeboxAction.Repeat:
                if (args.Index is < 0 or > 2) return;
                ent.Comp.Repeat = (JukeboxRepeat) args.Index; break;
            default: return;
        }
        Publish(ent);
    }

    private bool AddTracks(Entity<JukeboxComponent> ent, IEnumerable<string> tracks, bool first = false)
    {
        var valid = tracks.Where(id => _protoManager.HasIndex<JukeboxPrototype>(id)).ToArray();
        if (valid.Length == 0 || valid.Length + ent.Comp.Queue.Count > JukeboxRules.MaxQueue) return false;
        var entries = valid.Select(id => new JukeboxQueueEntry(Guid.NewGuid().ToString("N"), id));
        if (first) ent.Comp.Queue.InsertRange(0, entries); else ent.Comp.Queue.AddRange(entries);
        return true;
    }

    private void Advance(Entity<JukeboxComponent> ent, bool skipped)
    {
        var next = JukeboxRules.Advance(ent.Comp.Queue, ent.Comp.SelectedSongId?.Id, ent.Comp.Repeat, skipped);
        StopAudio(ent);
        ent.Comp.SelectedSongId = next;
        while (ent.Comp.SelectedSongId != null && !_protoManager.HasIndex(ent.Comp.SelectedSongId.Value))
        {
            ent.Comp.SelectedSongId = ent.Comp.Queue.Count > 0 ? ent.Comp.Queue[0].Track : null;
            if (ent.Comp.Queue.Count > 0) ent.Comp.Queue.RemoveAt(0);
        }
        Publish(ent);
        if (ent.Comp.SelectedSongId != null) Play(ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _updateTimer += frameTime;
        if (_updateTimer < 0.2f) return;
        var elapsed = _updateTimer; _updateTimer = 0;
        foreach (var uid in _active.ToArray())
        {
            if (!TryComp<JukeboxComponent>(uid, out var comp)) { _active.Remove(uid); continue; }
            if (comp.Selecting)
            {
                comp.SelectAccumulator += elapsed;
                if (comp.SelectAccumulator < 0.5f) continue;
                comp.Selecting = false;
                TryUpdateVisualState((uid, comp));
            }
            if (!TryComp<AudioComponent>(comp.AudioStream, out var audio)) { _active.Remove(uid); continue; }
            if (audio.State == AudioState.Playing && _protoManager.TryIndex(comp.SelectedSongId, out var track) &&
                (_timing.CurTime - audio.AudioStart).TotalSeconds >= Length(track)) Advance((uid, comp), false);
        }
    }

    private void OnTerminating(ref EntityTerminatingEvent args)
    {
        if (!_audioOwners.Remove(args.Entity.Owner, out var owner) || !TryComp<JukeboxComponent>(owner, out var comp) ||
            comp.AudioStream != args.Entity.Owner) return;
        comp.AudioStream = null;
        var wasActive = _active.Remove(owner);
        Dirty(owner, comp);
        if (wasActive && !TerminatingOrDeleted(owner)) Advance((owner, comp), false);
    }

    private void OnPower(Entity<JukeboxComponent> ent, ref PowerChangedEvent args)
    {
        if (!this.IsPowered(ent.Owner, EntityManager)) Stop(ent);
        TryUpdateVisualState(ent);
        Publish(ent);
    }

    private void TryUpdateVisualState(Entity<JukeboxComponent> ent) =>
        _appearanceSystem.SetData(ent.Owner, JukeboxVisuals.VisualState,
            this.IsPowered(ent.Owner, EntityManager) ? JukeboxVisualState.On : JukeboxVisualState.Off);
}

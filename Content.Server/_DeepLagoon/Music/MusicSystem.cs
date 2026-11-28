using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Content.Server.Administration.Managers;
using Content.Shared._DeepLagoon.Music;
using Content.Shared.GameTicking;
using Content.Shared.Administration;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects.Components;
using Content.Shared.Toggleable;
using Robust.Shared.Configuration;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Server.Player;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Music;

public sealed class MusicSystem : EntitySystem
{
    [Dependency] private readonly MusicApi _api = default!;
    [Dependency] private readonly MusicBroadcastSystem _broadcast = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IAdminManager _admins = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly ILogManager _log = default!;
    [Dependency] private readonly INetManager _net = default!;
    private INetManager NetManager => _net;

    private ISawmill _sawmill = default!;

    private readonly Dictionary<string, MusicChannel> _channels = new();

    private readonly Dictionary<string, MusicTrack> _known = new();

    private CancellationTokenSource _lifetime = new();

    private float _defaultGain;

    public bool Enabled => _cfg.GetCVar(MusicCVars.Enabled);

    public event Action? StateChanged;

    public sealed class MusicChannel
    {
        public readonly string Name;
        public readonly List<MusicTrack> Queue = new();

        public MusicTrack? Current;
        public byte[]? Audio;
        public MusicPlaybackStatus Status = MusicPlaybackStatus.Stopped;
        public float Position;
        public float Gain;
        public int Pending;
        public uint SourceNetEntity;
        public float MaxDistance = MusicConstants.SpeakerMaxDistance;
        public int RunId;
        public float StartAt;
        public float PublishTimer;

        public MusicChannel(string name, float gain)
        {
            Name = name;
            Gain = gain;
        }

        public bool IsGlobal => SourceNetEntity == 0;
    }

    private MusicChannel Channel(string name) => Channel(name, _defaultGain);

    private MusicChannel Channel(string name, float gain)
    {
        if (_channels.TryGetValue(name, out var existing))
            return existing;

        var created = new MusicChannel(name, gain);
        _channels[name] = created;
        return created;
    }

    public MusicChannel? Find(string name) => _channels.GetValueOrDefault(name);

    private void Remember(IEnumerable<MusicTrack> tracks)
    {
        foreach (var track in tracks)
        {
            if (_known.Count >= MusicConstants.MaxResults * 4)
                _known.Clear();

            _known[track.Id] = track;
        }
    }

    private MusicTrack Describe(string id)
    {
        if (_known.TryGetValue(id, out var known))
            return known;

        return new MusicTrack(id, id, string.Empty, 0f);
    }

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = _log.GetSawmill("dl.music");
        NetManager.RegisterNetMessage<MusicPlaybackNetMessage>();
        _defaultGain = MusicRules.ClampGain(_cfg.GetCVar(MusicCVars.GlobalGain));
        _cfg.OnValueChanged(MusicCVars.GlobalGain, value =>
        {
            _defaultGain = MusicRules.ClampGain(value);
            RefreshSpeakers();
            Publish();
        });

        SubscribeLocalEvent<MusicSpeakerComponent, MusicSearchRequestMessage>(OnSearch);
        SubscribeLocalEvent<MusicSpeakerComponent, MusicPlayRequestMessage>(OnPlay);
        SubscribeLocalEvent<MusicSpeakerComponent, MusicControlMessage>(OnControl);
        SubscribeLocalEvent<MusicSpeakerComponent, MusicSetGainMessage>(OnGain);
        SubscribeLocalEvent<MusicSpeakerComponent, MusicSetTimeMessage>(OnSetTime);
        SubscribeLocalEvent<MusicSpeakerComponent, BoundUIOpenedEvent>(OnOpened);

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawned);
    }

    private static string ChannelFor(EntityUid speaker, IEntityManager entities) =>
        MusicChannels.Speaker((uint) entities.GetNetEntity(speaker).Id);

    public override void Shutdown()
    {
        base.Shutdown();
        _lifetime.Cancel();
        _lifetime.Dispose();

        foreach (var channel in _channels.Values)
            _broadcast.SendControlAll(channel.Name, MusicPlaybackCommand.Stop);
    }

    public bool IsAdmin(ICommonSession? session) =>
        session != null && _admins.HasAdminFlag(session, AdminFlags.Admin);

    private void OnSearch(Entity<MusicSpeakerComponent> ent, ref MusicSearchRequestMessage args)
    {
        var query = args.Query;
        _ = SearchForActorAsync(ent.Owner, args.Actor, query);
    }

    private async Task SearchForActorAsync(EntityUid speaker, EntityUid actor, string query)
    {
        var (tracks, status) = await _api.SearchAsync(query, _lifetime.Token);
        Remember(tracks);

        Reply(speaker, new MusicSearchResultMessage(tracks.ToArray(), status, query), actor);
    }

    private void OnPlay(Entity<MusicSpeakerComponent> ent, ref MusicPlayRequestMessage args)
    {
        if (!MusicRules.ValidTrackId(args.TrackId, out var id))
            return;

        Play(ChannelFor(ent.Owner, EntityManager), (uint) GetNetEntity(ent.Owner).Id, id, string.Empty, string.Empty);
    }

    public void PlayGlobal(string trackId, string title, string artist) =>
        Play(MusicChannels.Global, 0, trackId, title, artist);

    public async Task<bool> PlayGlobalAsync(string trackId, string title, string artist) =>
        await PlayAsync(MusicChannels.Global, 0, trackId, title, artist);

    private void Play(string channelName, uint sourceNetEntity, string trackId, string title, string artist)
    {
        _ = PlayAsync(channelName, sourceNetEntity, trackId, title, artist);
    }

    private async Task<bool> PlayAsync(string channelName, uint sourceNetEntity, string trackId, string title,
        string artist)
    {
        if (!Enabled || !MusicRules.ValidTrackId(trackId, out var id))
            return false;

        var channel = Channel(channelName);
        var run = ++channel.RunId;

        var decoded = await _api.FetchAsync(id, _lifetime.Token);
        if (decoded == null || run != channel.RunId)
            return false;

        var wav = await Task.Run(() => MusicTranscoder.WrapWav(decoded.Pcm, decoded.SampleRate, decoded.Channels));
        if (run != channel.RunId)
            return false;

        channel.Audio = wav;
        channel.SourceNetEntity = sourceNetEntity;
        channel.MaxDistance = MusicConstants.SpeakerMaxDistance;

        var meta = Describe(id);
        var shownTitle = MusicRules.Clean(title, MusicConstants.MaxTitleLength);
        var shownArtist = MusicRules.Clean(artist, MusicConstants.MaxArtistLength);
        channel.Current = new MusicTrack(
            id,
            shownTitle.Length > 0 ? shownTitle : meta.Title,
            shownArtist.Length > 0 ? shownArtist : meta.Artist,
            decoded.Duration);
        channel.Pending = IoCManager.Resolve<IPlayerManager>().PlayerCount;
        channel.Position = 0f;
        channel.Status = MusicPlaybackStatus.Downloading;
        Publish();
        RefreshSpeakers();

        var delay = sourceNetEntity == 0 ? _broadcast.SlowestSeconds() : MusicRules.StartDelay(0f);
        var sent = await _broadcast.BroadcastAsync(channelName, id, wav, delay, channel.Gain, sourceNetEntity,
            channel.MaxDistance);

        if (run != channel.RunId)
            return false;

        if (sent == 0 && channel.Pending > 0)
        {
            _sawmill.Warning("Music {Track} on {Channel} reached no listeners", id, channelName);
            channel.Status = MusicPlaybackStatus.Stopped;
            channel.Current = null;
            channel.Audio = null;
            Publish();
            RefreshSpeakers();
            return false;
        }

        channel.StartAt = delay;
        channel.Status = MusicPlaybackStatus.Playing;
        Publish();
        return true;
    }

    public async Task<(MusicTrack[] Tracks, MusicSearchStatus Status)> SearchAsync(string query)
    {
        var (tracks, status) = await _api.SearchAsync(query, _lifetime.Token);
        Remember(tracks);
        return (tracks.ToArray(), status);
    }

    private void OnControl(Entity<MusicSpeakerComponent> ent, ref MusicControlMessage args)
    {
        var channel = ChannelFor(ent.Owner, EntityManager);

        switch (args.Action)
        {
            case MusicControlAction.Pause: Pause(channel); break;
            case MusicControlAction.Resume: Resume(channel); break;
            case MusicControlAction.Stop: Stop(channel); break;
            case MusicControlAction.Next: Next(channel, (uint) GetNetEntity(ent.Owner).Id); break;
        }
    }

    private void OnGain(Entity<MusicSpeakerComponent> ent, ref MusicSetGainMessage args) =>
        SetGain(ChannelFor(ent.Owner, EntityManager), args.Gain);

    private void OnSetTime(Entity<MusicSpeakerComponent> ent, ref MusicSetTimeMessage args)
    {
        var channelName = ChannelFor(ent.Owner, EntityManager);
        var channel = Find(channelName);
        if (channel?.Current == null)
            return;

        var position = MusicRules.ClampPosition(args.Position, channel.Current.Duration);
        channel.Position = position;
        _broadcast.SendControl(channelName, MusicPlaybackCommand.Seek, position, channel.Gain,
            channel.SourceNetEntity, channel.MaxDistance);
        Publish();
    }

    public void Pause(string channelName)
    {
        var channel = Find(channelName);
        if (channel == null || channel.Status != MusicPlaybackStatus.Playing)
            return;

        channel.StartAt = 0f;
        channel.Status = MusicPlaybackStatus.Paused;
        _broadcast.SendControl(channelName, MusicPlaybackCommand.Pause, channel.Position, channel.Gain,
            channel.SourceNetEntity, channel.MaxDistance);
        Publish();
        RefreshSpeakers();
    }

    public void Resume(string channelName)
    {
        var channel = Find(channelName);
        if (channel == null || channel.Status != MusicPlaybackStatus.Paused)
            return;

        channel.StartAt = 0f;
        channel.Status = MusicPlaybackStatus.Playing;
        _broadcast.SendControl(channelName, MusicPlaybackCommand.Play, channel.Position, channel.Gain,
            channel.SourceNetEntity, channel.MaxDistance);
        Publish();
        RefreshSpeakers();
    }

    public void Stop(string channelName)
    {
        var channel = Find(channelName);
        if (channel == null)
            return;

        channel.RunId++;
        channel.Current = null;
        channel.Audio = null;
        channel.Pending = 0;
        channel.Position = 0f;
        channel.StartAt = 0f;
        channel.PublishTimer = 0f;
        channel.Status = MusicPlaybackStatus.Stopped;
        _broadcast.SendControlAll(channelName, MusicPlaybackCommand.Stop);
        Publish();
        RefreshSpeakers();
    }

    public void Next(string channelName, uint speakerNetEntity)
    {
        var channel = Find(channelName);
        if (channel == null)
            return;

        if (channel.Queue.Count == 0)
        {
            Stop(channelName);
            return;
        }

        var next = channel.Queue[0];
        channel.Queue.RemoveAt(0);

        channel.Status = MusicPlaybackStatus.Downloading;
        channel.Position = 0f;
        Publish();

        _ = PlayNextAsync(channelName, next, speakerNetEntity, channel.RunId + 1);
    }

    private async Task PlayNextAsync(string channelName, MusicTrack next, uint speakerNetEntity, int expectedRun)
    {
        if (await PlayAsync(channelName, speakerNetEntity, next.Id, next.Title, next.Artist))
            return;

        var channel = Find(channelName);
        if (channel == null || channel.RunId != expectedRun || channel.Status != MusicPlaybackStatus.Downloading)
            return;

        if (channel.Queue.Count == 0)
            return;

        var following = channel.Queue[0];
        channel.Queue.RemoveAt(0);
        await PlayNextAsync(channelName, following, speakerNetEntity, channel.RunId + 1);
    }

    public void Enqueue(string channelName, MusicTrack track)
    {
        var channel = Channel(channelName);

        if (channel.Queue.Count >= Math.Clamp(_cfg.GetCVar(MusicCVars.QueueLimit), 1, MusicConstants.MaxQueue))
            channel.Queue.RemoveAt(0);

        channel.Queue.Add(track);
    }

    public void SetGain(string channelName, float gain)
    {
        var channel = Find(channelName);
        if (channel == null)
            return;

        channel.Gain = MusicRules.ClampGain(gain);
        _broadcast.SendControl(channelName, MusicPlaybackCommand.Gain, channel.Position, channel.Gain,
            channel.SourceNetEntity, channel.MaxDistance);
        Publish();
        RefreshSpeakers();
    }

    public int ListenerCount => IoCManager.Resolve<IPlayerManager>().PlayerCount;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var dirty = false;

        foreach (var channel in _channels.Values)
        {
            if (channel.Status != MusicPlaybackStatus.Playing)
                continue;

            var step = frameTime;

            if (channel.StartAt > 0f)
            {
                channel.StartAt -= step;

                if (channel.StartAt > 0f)
                    continue;

                step = -channel.StartAt;
                channel.StartAt = 0f;
            }

            channel.Position += step;

            if (channel.Current != null && channel.Current.Duration > 0f &&
                channel.Position >= channel.Current.Duration)
            {
                Next(channel.Name, channel.SourceNetEntity);
                continue;
            }

            channel.PublishTimer += step;
            if (channel.PublishTimer < 1f)
                continue;

            channel.PublishTimer = 0f;
            dirty = true;
        }

        if (dirty)
            Publish();
    }

    private void OnOpened(Entity<MusicSpeakerComponent> ent, ref BoundUIOpenedEvent args)
    {
        var channel = ChannelFor(ent.Owner, EntityManager);
        _ui.SetUiState(ent.Owner, MusicUiKey.Key, BuildState(channel));

        Reply(ent.Owner, new MusicSearchResultMessage([], MusicSearchStatus.Ok), args.Actor);
    }

    public MusicState BuildState(string channelName) => BuildState(Channel(channelName));

    private MusicState BuildState(MusicChannel channel) => new(channel.Status, channel.Current, channel.Position,
        channel.Current?.Duration ?? 0f, channel.Gain, channel.IsGlobal, channel.Pending, channel.Pending);

    private void Publish()
    {
        StateChanged?.Invoke();

        var query = EntityQueryEnumerator<MusicSpeakerComponent, UserInterfaceComponent>();
        while (query.MoveNext(out var uid, out _, out var ui))
        {
            if (ui.ClientOpenInterfaces.Count == 0)
                continue;

            var channel = Channel(ChannelFor(uid, EntityManager));
            _ui.SetUiState(uid, MusicUiKey.Key, BuildState(channel));
        }
    }

    private void RefreshSpeakers()
    {
        var query = EntityQueryEnumerator<MusicSpeakerComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            var channel = Find(ChannelFor(uid, EntityManager));
            var active = channel is { Status: MusicPlaybackStatus.Playing } &&
                         (channel.SourceNetEntity == 0 || channel.SourceNetEntity == (uint) GetNetEntity(uid).Id);
            _appearance.SetData(uid, ToggleableVisuals.Enabled, active);
        }
    }

    private void Reply(EntityUid speaker, BoundUserInterfaceMessage message, EntityUid actor) =>
        _ui.ServerSendUiMessage(speaker, MusicUiKey.Key, message, actor);

    public MusicEuiState BuildEuiState() => BuildEuiState(MusicChannels.Global);

    public MusicEuiState BuildEuiState(string channelName)
    {
        var channel = Find(channelName);

        if (channel == null)
        {
            return new MusicEuiState("stopped", null, _defaultGain, 0f, 0f, Enabled, ListenerCount, 0);
        }

        return new MusicEuiState(
            channel.Status.ToString().ToLowerInvariant(),
            channel.Current,
            channel.Gain,
            channel.Current?.Duration ?? 0f,
            channel.Position,
            Enabled,
            ListenerCount,
            channel.Queue.Count);
    }

    private void OnPlayerSpawned(PlayerSpawnCompleteEvent args)
    {
        foreach (var channel in _channels.Values)
        {
            if (channel.Status is not (MusicPlaybackStatus.Playing or MusicPlaybackStatus.Paused) ||
                channel.Audio == null || channel.Current == null)
                continue;

            if (!_broadcast.InRange(args.Player, channel.SourceNetEntity, channel.MaxDistance))
                continue;

            _ = ResendToAsync(args.Player, channel);
        }
    }

    private async Task ResendToAsync(ICommonSession session, MusicChannel channel)
    {
        var audio = channel.Audio;
        var track = channel.Current;
        if (audio == null || track == null || !session.Channel.IsConnected)
            return;

        var run = channel.RunId;

        if (!await _broadcast.SendTrackAsync(session, channel.Name, track.Id, audio, channel.Gain,
                channel.SourceNetEntity, channel.MaxDistance))
            return;

        if (run != channel.RunId || !session.Channel.IsConnected)
            return;

        var position = Math.Max(0f, channel.Position);
        var command = channel.Status == MusicPlaybackStatus.Paused
            ? MusicPlaybackCommand.Pause
            : MusicPlaybackCommand.Play;

        _broadcast.SendControl(session, channel.Name, command, position, channel.Gain, channel.SourceNetEntity,
            channel.MaxDistance);
    }
}

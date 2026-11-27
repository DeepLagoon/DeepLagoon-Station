using System.Threading;
using System.Threading.Tasks;
using Content.Server.Administration.Managers;
using Content.Shared._DeepLagoon.Music;
using Content.Shared.GameTicking;
using Content.Shared.Administration;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.Toggleable;
using Robust.Shared.Configuration;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Server.Player;
using Robust.Shared.Player;
using Robust.Shared.Timing;

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
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly INetManager _net = default!;
    private INetManager NetManager => _net;

    private ISawmill _sawmill = default!;

    private readonly List<MusicTrack> _queue = new();

    private readonly Dictionary<string, MusicTrack> _known = new();

    private CancellationTokenSource _lifetime = new();

    private MusicTrack? _current;
    private byte[]? _currentAudio;
    private float _gain;
    private int _pending;
    public MusicPlaybackStatus Status { get; private set; } = MusicPlaybackStatus.Stopped;
    public MusicTrack? Current => _current;
    public float Gain => _gain;
    public bool Enabled => _cfg.GetCVar(MusicCVars.Enabled);

    public event Action? StateChanged;

    private float _clientPosition;

    private uint _sourceNetEntity;

    private float _maxDistance = MusicConstants.SpeakerMaxDistance;

    private int _runId;

    private float _startAt;

    private float _publishTimer;

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
        _gain = MusicRules.ClampGain(_cfg.GetCVar(MusicCVars.GlobalGain));
        _cfg.OnValueChanged(MusicCVars.GlobalGain, value =>
        {
            _gain = MusicRules.ClampGain(value);
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

    private void OnPlayerSpawned(PlayerSpawnCompleteEvent args)
    {
        if (Status is not (MusicPlaybackStatus.Playing or MusicPlaybackStatus.Paused) ||
            _currentAudio == null || _current == null)
            return;

        _ = ResendToAsync(args.Player);
    }

    private async Task ResendToAsync(ICommonSession session)
    {
        var audio = _currentAudio;
        var track = _current;
        if (audio == null || track == null || !session.Channel.IsConnected)
            return;

        var run = _runId;

        if (!await _broadcast.SendTrackAsync(session, track.Id, audio, _gain, _sourceNetEntity, _maxDistance))
            return;

        if (run != _runId || !session.Channel.IsConnected)
            return;

        var position = Math.Max(0f, _clientPosition);
        var paused = Status == MusicPlaybackStatus.Paused;

        if (paused)
        {
            _broadcast.SendControl(session, MusicPlaybackCommand.Pause, position, _gain, _sourceNetEntity,
                _maxDistance);
            return;
        }

        _broadcast.SendControl(session, MusicPlaybackCommand.Play, position, _gain, _sourceNetEntity, _maxDistance);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _lifetime.Cancel();
        _lifetime.Dispose();
        Stop();
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

        _ = PlayForActorAsync(ent.Owner, args.Actor, id);
    }

    private async Task PlayForActorAsync(EntityUid speaker, EntityUid actor, string id)
    {
        if (!Enabled)
            return;

        var run = ++_runId;

        var decoded = await _api.FetchAsync(id, _lifetime.Token);
        if (decoded == null || run != _runId)
            return;

        var wav = await WrapOffThreadAsync(decoded);
        if (run != _runId)
            return;

        _currentAudio = wav;
        var meta = Describe(id);
        _current = new MusicTrack(id, meta.Title, meta.Artist, decoded.Duration);

        _sourceNetEntity = (uint) GetNetEntity(speaker).Id;
        _maxDistance = MusicConstants.SpeakerMaxDistance;

        _pending = IoCManager.Resolve<IPlayerManager>().PlayerCount;
        _clientPosition = 0f;
        Status = MusicPlaybackStatus.Downloading;
        Publish();
        RefreshSpeakers();

        var delay = MusicRules.StartDelay(0f);
        var sent = await _broadcast.BroadcastAsync(id, wav, delay, _gain, _sourceNetEntity, _maxDistance);

        if (run != _runId)
            return;

        if (sent == 0 && _pending > 0)
        {
            _sawmill.Warning("Music {Track} reached no listeners", id);
            Status = MusicPlaybackStatus.Stopped;
            _current = null;
            _currentAudio = null;
            Publish();
            RefreshSpeakers();
            return;
        }

        _startAt = delay;
        Status = MusicPlaybackStatus.Playing;
        Publish();
    }

    public async Task<(MusicTrack[] Tracks, MusicSearchStatus Status)> SearchAsync(string query)
    {
        var (tracks, status) = await _api.SearchAsync(query, _lifetime.Token);
        Remember(tracks);
        return (tracks.ToArray(), status);
    }

    private static Task<byte[]> WrapOffThreadAsync(DecodedTrack decoded) =>
        Task.Run(() => MusicTranscoder.WrapWav(decoded.Pcm, decoded.SampleRate, decoded.Channels));

    public async Task<bool> PlayGlobalAsync(string trackId, string title, string artist)
    {
        if (!Enabled || !MusicRules.ValidTrackId(trackId, out var id))
            return false;

        var run = ++_runId;

        var decoded = await _api.FetchAsync(id, _lifetime.Token);
        if (decoded == null || run != _runId)
            return false;

        var wav = await WrapOffThreadAsync(decoded);
        if (run != _runId)
            return false;

        _currentAudio = wav;

        _sourceNetEntity = 0;
        _maxDistance = MusicConstants.SpeakerMaxDistance;

        var meta = Describe(id);
        var shownTitle = MusicRules.Clean(title, MusicConstants.MaxTitleLength);
        var shownArtist = MusicRules.Clean(artist, MusicConstants.MaxArtistLength);
        _current = new MusicTrack(
            id,
            shownTitle.Length > 0 ? shownTitle : meta.Title,
            shownArtist.Length > 0 ? shownArtist : meta.Artist,
            decoded.Duration);
        _pending = IoCManager.Resolve<IPlayerManager>().PlayerCount;
        _clientPosition = 0f;
        Status = MusicPlaybackStatus.Downloading;
        Publish();
        RefreshSpeakers();

        var delay = _broadcast.SlowestSeconds();
        var sent = await _broadcast.BroadcastAsync(id, wav, delay, _gain, _sourceNetEntity, _maxDistance);

        if (run != _runId)
            return false;

        if (sent == 0 && _pending > 0)
        {
            _sawmill.Warning("Music {Track} reached no listeners", id);
            Status = MusicPlaybackStatus.Stopped;
            _current = null;
            _currentAudio = null;
            Publish();
            RefreshSpeakers();
            return false;
        }

        _startAt = delay;
        Status = MusicPlaybackStatus.Playing;
        Publish();
        return true;
    }

    private void OnControl(Entity<MusicSpeakerComponent> ent, ref MusicControlMessage args)
    {
        switch (args.Action)
        {
            case MusicControlAction.Pause: Pause(); break;
            case MusicControlAction.Resume: Resume(); break;
            case MusicControlAction.Stop: Stop(); break;
            case MusicControlAction.Next: Next(); break;
        }
    }

    private void OnGain(Entity<MusicSpeakerComponent> ent, ref MusicSetGainMessage args) => SetGain(args.Gain);

    private void OnSetTime(Entity<MusicSpeakerComponent> ent, ref MusicSetTimeMessage args)
    {
        if (_current == null)
            return;

        var position = MusicRules.ClampPosition(args.Position, _current.Duration);
        _clientPosition = position;
        _broadcast.SendControl(MusicPlaybackCommand.Seek, position, _gain, _sourceNetEntity, _maxDistance);
        Publish();
    }

    public void Pause()
    {
        if (Status != MusicPlaybackStatus.Playing)
            return;

        _startAt = 0f;
        Status = MusicPlaybackStatus.Paused;
        _broadcast.SendControl(MusicPlaybackCommand.Pause);
        Publish();
        RefreshSpeakers();
    }

    public void Resume()
    {
        if (Status != MusicPlaybackStatus.Paused)
            return;

        _startAt = 0f;
        Status = MusicPlaybackStatus.Playing;
        _broadcast.SendControl(MusicPlaybackCommand.Play, _clientPosition, _gain, _sourceNetEntity, _maxDistance);
        Publish();
        RefreshSpeakers();
    }

    public void Stop()
    {
        _runId++;
        _current = null;
        _currentAudio = null;
        _pending = 0;
        _clientPosition = 0f;
        _startAt = 0f;
        _publishTimer = 0f;
        Status = MusicPlaybackStatus.Stopped;
        _broadcast.SendControl(MusicPlaybackCommand.Stop);
        Publish();
        RefreshSpeakers();
    }

    public void Next()
    {
        if (_queue.Count == 0)
        {
            Stop();
            return;
        }

        var next = _queue[0];
        _queue.RemoveAt(0);

        Status = MusicPlaybackStatus.Downloading;
        _clientPosition = 0f;
        Publish();

        _ = PlayNextAsync(next, _runId + 1);
    }

    private async Task PlayNextAsync(MusicTrack next, int expectedRun)
    {
        if (await PlayGlobalAsync(next.Id, next.Title, next.Artist))
            return;

        if (_runId != expectedRun || Status != MusicPlaybackStatus.Downloading)
            return;

        if (_queue.Count == 0)
            return;

        var following = _queue[0];
        _queue.RemoveAt(0);
        await PlayNextAsync(following, _runId + 1);
    }

    public void Enqueue(MusicTrack track)
    {
        if (_queue.Count >= Math.Clamp(_cfg.GetCVar(MusicCVars.QueueLimit), 1, MusicConstants.MaxQueue))
            _queue.RemoveAt(0);

        _queue.Add(track);
    }

    public void SetGain(float gain)
    {
        _gain = MusicRules.ClampGain(gain);
        _broadcast.SendControl(MusicPlaybackCommand.Gain, _clientPosition, _gain);
        Publish();
        RefreshSpeakers();
    }

    public int ListenerCount => IoCManager.Resolve<IPlayerManager>().PlayerCount;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (Status != MusicPlaybackStatus.Playing)
            return;

        if (_startAt > 0f)
        {
            _startAt -= frameTime;

            if (_startAt > 0f)
                return;

            frameTime = -_startAt;
            _startAt = 0f;
        }

        _clientPosition += frameTime;

        if (_current != null && _current.Duration > 0f && _clientPosition >= _current.Duration)
        {
            Next();
            return;
        }

        _publishTimer += frameTime;
        if (_publishTimer < 1f)
            return;

        _publishTimer = 0f;
        Publish();
    }

    private void OnOpened(Entity<MusicSpeakerComponent> ent, ref BoundUIOpenedEvent args)
    {
        _ui.SetUiState(ent.Owner, MusicUiKey.Key, BuildState());

        Reply(ent.Owner, new MusicSearchResultMessage([], MusicSearchStatus.Ok), args.Actor);
    }

    public float Position => _clientPosition;

    public MusicState BuildState() => new(Status, _current, _clientPosition, _current?.Duration ?? 0f, _gain,
        IsGlobal, _pending, _pending);

    public bool IsGlobal { get; private set; } = true;

    private void Publish()
    {
        StateChanged?.Invoke();

        var state = BuildState();
        var query = EntityQueryEnumerator<MusicSpeakerComponent, UserInterfaceComponent>();
        while (query.MoveNext(out var uid, out _, out var ui))
        {
            if (ui.ClientOpenInterfaces.Count == 0)
                continue;

            _ui.SetUiState(uid, MusicUiKey.Key, state);
        }
    }

    private void RefreshSpeakers()
    {
        var playing = Status == MusicPlaybackStatus.Playing;
        var query = EntityQueryEnumerator<MusicSpeakerComponent>();
        while (query.MoveNext(out var uid, out _))
            _appearance.SetData(uid, ToggleableVisuals.Enabled, playing);
    }

    private void Reply(EntityUid speaker, BoundUserInterfaceMessage message, EntityUid actor) =>
        _ui.ServerSendUiMessage(speaker, MusicUiKey.Key, message, actor);

    public MusicEuiState BuildEuiState() => new(
        Status.ToString().ToLowerInvariant(),
        _current,
        _gain,
        _current?.Duration ?? 0f,
        _clientPosition,
        Enabled,
        ListenerCount,
        _queue.Count);
}

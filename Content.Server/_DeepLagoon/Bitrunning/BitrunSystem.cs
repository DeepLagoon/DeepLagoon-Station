using System.Linq;
using Content.Server._DeepLagoon.Mapping;
using Content.Server.GameTicking;
using Content.Server.Mind;
using Content.Server.Power.EntitySystems;
using Content.Server.Research.Systems;
using Content.Server.Station.Systems;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Mobs.Components;
using Content.Shared.Research.Components;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunVirtualEntityComponent : Component
{
    public EntityUid Domain;
}

public sealed partial class BitrunSystem : EntitySystem
{
    private const float EquipmentRange = 4f;
    private const float GoalRange = 0.7f;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan StopWarning = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ResultGrace = TimeSpan.FromSeconds(60);

    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private GridEnvironmentSystem _environment = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ResearchSystem _research = default!;
    [Dependency] private StationSystem _stations = default!;

    private readonly Dictionary<EntityUid, Run> _runs = new();
    private readonly Dictionary<EntityUid, Run> _domains = new();
    private readonly Dictionary<EntityUid, string> _results = new();
    private readonly Dictionary<EntityUid, Guid> _latestRuns = new();
    private readonly Dictionary<Guid, Payout> _payouts = new();
    private TimeSpan _nextPoll;

    public override void Initialize()
    {
        InitializeUi();
        InitializeAvatars();
        InitializeObjectives();
        InitializeGlitches();
        InitializeRewards();
        _configuration.OnValueChanged(BitrunCVars.LobbyIntervalSeconds, OnLobbyIntervalChanged, invokeImmediately: true);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => ResetRound());
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRoundLevelChanged);
        SubscribeLocalEvent<EntParentChangedMessage>(OnVirtualMapChanged);
        SubscribeLocalEvent<BitrunServerComponent, EntityTerminatingEvent>(OnServerTerminating, before: [typeof(MindSystem)]);
    }

    public override void Shutdown()
    {
        _configuration.UnsubValueChanged(BitrunCVars.LobbyIntervalSeconds, OnLobbyIntervalChanged);
        base.Shutdown();
    }

    private void OnRoundLevelChanged(GameRunLevelChangedEvent args)
    {
        if (args.New == GameRunLevel.InRound)
        {
            _lobbyEpoch = _timing.CurTime;
            foreach (var lobby in _lobbies.Values)
                lobby.StartAt = NextLobbyStart();
        }

        if (args.New != GameRunLevel.PostRound)
            return;

        foreach (var lobby in _lobbies.Values.ToArray())
            CancelLobby(lobby);

        foreach (var run in _runs.Values.ToArray())
            Finish(run);
    }

    private void ResetRound()
    {
        foreach (var lobby in _lobbies.Values.ToArray())
            CancelLobby(lobby);

        foreach (var run in _runs.Values.ToArray())
            Finish(run);

        _results.Clear();
        _latestRuns.Clear();
        _payouts.Clear();
        _personalCredits.Clear();
        _personalResearchPoints.Clear();
        _nextPoll = TimeSpan.Zero;
        _lobbyEpoch = _timing.CurTime;
    }

    public override void Update(float frameTime)
    {
        if (_timing.CurTime < _nextPoll)
            return;

        _nextPoll = _timing.CurTime + PollInterval;
        foreach (var lobby in _lobbies.Values.ToArray())
            PollLobby(lobby);

        foreach (var run in _runs.Values.ToArray())
            PollRun(run);

        foreach (var payout in _payouts.Values.ToArray())
            ProcessPayout(payout);

        RefreshOpenConsoles();
    }

    private void OnServerTerminating(Entity<BitrunServerComponent> ent, ref EntityTerminatingEvent args)
    {
        if (_lobbies.TryGetValue(ent.Owner, out var lobby))
            CancelLobby(lobby);

        if (_runs.TryGetValue(ent.Owner, out var run))
            Finish(run);

        _results.Remove(ent.Owner);
        _latestRuns.Remove(ent.Owner);
    }

    private void OnVirtualMapChanged(ref EntParentChangedMessage args)
    {
        var entity = args.Entity;
        if (args.Transform.MapUid is not { } next || args.OldMapId == next ||
            TerminatingOrDeleted(entity) || HasComp<GhostComponent>(entity))
            return;

        if (args.OldMapId == null && _domains.ContainsKey(next))
            EnsureComp<BitrunVirtualEntityComponent>(entity).Domain = next;

        // Visitors teleported in by admins keep their real bodies. Simulated entities stay in the run.
        if (TryComp<BitrunVirtualEntityComponent>(entity, out var simulated) && simulated.Domain != next &&
            !HasComp<BitrunAvatarComponent>(entity))
            QueueDel(entity);
    }

    private bool TryLaunch(EntityUid server, BitrunServerComponent machine, BitrunDomainPrototype domain,
        EntityUid receiver, bool random, Guid session)
    {
        if (!CanScheduleRun(server, machine, domain, receiver))
            return false;

        var map = _maps.CreateMap(out var mapId);
        _maps.SetAmbientLight(mapId, Color.White);
        var retained = false;
        try
        {
            if (!_loader.TryLoadGridTracked(mapId, domain.Map, out var grid))
                return false;

            var environment = EnsureComp<GridEnvironmentComponent>(grid.Value.Owner);
            environment.SafeAtmosphere = true;
            _environment.RefreshGrid(grid.Value.Owner, environment);

            var run = new Run(session, server, receiver, map, mapId, domain, _timing.CurTime, random);
            run.Spawns.AddRange(FindMapEntities<BitrunAvatarSpawnComponent>(map));
            run.Goals.AddRange(FindMapEntities<BitrunCacheGoalComponent>(map));
            run.CacheSpawns.AddRange(FindMapEntities<BitrunCacheSpawnComponent>(map));
            if (run.Spawns.Count == 0 || run.Goals.Count == 0 || run.CacheSpawns.Count == 0 ||
                FindMapEntities<BitrunExitComponent>(map).Count == 0)
            {
                Log.Error($"Bitrun domain {domain.ID} lacks a spawn, cache, goal or exit marker");
                return false;
            }

            (run.AttackerSpawns, run.DefenderSpawns) = FindTeamSpawns(grid.Value, run.Spawns, run.CacheSpawns);
            if (run.AttackerSpawns.Count == 0 || run.DefenderSpawns.Count == 0)
            {
                Log.Error($"Bitrun domain {domain.ID} lacks safe starting positions for both sides");
                return false;
            }

            ConfigureObjectives(run);
            if (domain.Objective == BitrunObjective.Cache)
                SpawnCache(run);

            foreach (var entity in FindMapEntities<MetaDataComponent>(map))
            {
                if (entity != map && entity != grid.Value.Owner)
                    EnsureComp<BitrunVirtualEntityComponent>(entity).Domain = map;
            }

            _runs.Add(server, run);
            _domains.Add(map, run);
            retained = true;
            _results.Remove(server);
            _latestRuns[server] = run.Id;
            _appearance.SetData(server, BitrunMachineVisuals.Active, true);
            Log.Info($"Bitrun {run.Id} launched {domain.ID} from {server} for R&D {receiver}");
            return true;
        }
        catch (Exception error)
        {
            Log.Error($"Could not load bitrun domain {domain.ID}: {error}");
            return false;
        }
        finally
        {
            if (!retained && !TerminatingOrDeleted(map))
                _maps.DeleteMap(mapId);
        }
    }

    private List<EntityUid> FindMapEntities<T>(EntityUid map) where T : IComponent
    {
        var result = new List<EntityUid>();
        var query = AllEntityQuery<T, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.MapUid == map)
                result.Add(uid);
        }

        return result;
    }

    private void PollRun(Run run)
    {
        if (!IsOnline(run.Server) || TerminatingOrDeleted(run.Map) ||
            run.StopAt is { } stop && _timing.CurTime >= stop)
        {
            Finish(run);
            return;
        }

        foreach (var avatar in run.Avatars.ToArray())
            CheckAvatar(run, avatar);

        if (run.Completed || run.StopAt != null || ResolveTeams(run))
            return;

        PollObjectives(run);
        if (run.Cache is { } lostCache && TerminatingOrDeleted(lostCache))
        {
            run.Cache = null;
            SpawnCache(run);
        }

        if (run.Cache is not { } cache || Transform(cache).MapUid != run.Map)
            return;

        var coordinates = Transform(cache).Coordinates;
        foreach (var goal in run.Goals)
        {
            if (!TerminatingOrDeleted(goal) &&
                coordinates.TryDistance(EntityManager, Transform(goal).Coordinates, out var distance) &&
                distance <= GoalRange)
            {
                TryComplete(run, cache);
                return;
            }
        }
    }

    private void SpawnCache(Run run, EntityCoordinates? coordinates = null)
    {
        if (run.Cache != null || run.Completed)
            return;

        if (coordinates == null)
        {
            var available = run.CacheSpawns.Where(uid => !TerminatingOrDeleted(uid)).ToArray();
            if (available.Length == 0)
                return;

            coordinates = Transform(_random.Pick(available)).Coordinates;
        }

        var cache = Spawn("BitrunEncryptedCache", coordinates.Value);
        EnsureComp<BitrunVirtualEntityComponent>(cache).Domain = run.Map;
        run.Cache = cache;
    }

    private void BeginStop(Run run)
    {
        if (run.Avatars.Count == 0)
        {
            Finish(run);
            return;
        }

        var deadline = _timing.CurTime + StopWarning;
        if (run.StopAt is { } stop && stop <= deadline)
            return;

        run.StopAt = deadline;
        NotifyAvatars(run, Loc.GetString("bitrun-stop-warning"));
    }

    private void Finish(Run run)
    {
        if (!_runs.Remove(run.Server))
            return;

        foreach (var avatar in run.Avatars.ToArray())
            Disconnect(avatar, false);

        if (!run.Completed && Exists(run.Server))
            _results[run.Server] = Loc.GetString("bitrun-aborted");

        _domains.Remove(run.Map);
        if (!TerminatingOrDeleted(run.Map))
            _maps.DeleteMap(run.MapId);

        if (!TerminatingOrDeleted(run.Server))
            _appearance.SetData(run.Server, BitrunMachineVisuals.Active, false);
    }

    private bool IsOnline(EntityUid uid) => !TerminatingOrDeleted(uid) && this.IsPowered(uid, EntityManager);

    private bool IsNearby(EntityUid origin, EntityUid candidate, float radius)
    {
        var first = Transform(origin);
        var second = Transform(candidate);
        return first.GridUid != null && first.GridUid == second.GridUid &&
               first.Coordinates.TryDistance(EntityManager, second.Coordinates, out var distance) && distance <= radius;
    }

    private EntityUid? FindEquipment<T>(EntityUid origin, float radius) where T : IComponent
    {
        EntityUid? nearest = null;
        var distance = radius;
        var query = EntityQueryEnumerator<T, TransformComponent>();
        var xform = Transform(origin);
        if (xform.GridUid == null)
            return null;

        while (query.MoveNext(out var uid, out _, out var candidate))
        {
            if (candidate.GridUid != xform.GridUid || !candidate.Anchored || !IsOnline(uid) ||
                !xform.Coordinates.TryDistance(EntityManager, candidate.Coordinates, out var delta) || delta > distance)
                continue;

            if (delta == distance && nearest is { } found && uid.CompareTo(found) > 0)
                continue;

            nearest = uid;
            distance = delta;
        }

        return nearest;
    }

    private bool CanUseResearch(EntityUid console, EntityUid receiver)
    {
        if (TerminatingOrDeleted(receiver) || !HasComp<ResearchServerComponent>(receiver))
            return false;

        var station = _stations.GetOwningStation(console);
        return station is { } owner
            ? _stations.GetOwningStation(receiver) == owner
            : Transform(console).GridUid is { } grid && Transform(receiver).GridUid == grid;
    }

    private sealed class Run(Guid id, EntityUid server, EntityUid receiver, EntityUid map, MapId mapId,
        BitrunDomainPrototype domain, TimeSpan started, bool random)
    {
        public readonly Guid Id = id;
        public readonly EntityUid Server = server;
        public readonly EntityUid Receiver = receiver;
        public readonly EntityUid Map = map;
        public readonly MapId MapId = mapId;
        public readonly BitrunDomainPrototype Domain = domain;
        public readonly TimeSpan Started = started;
        public readonly TimeSpan EndsAt = started + RunDuration;
        public readonly bool Random = random;
        public readonly Dictionary<NetUserId, Participant> Roster = new();
        public readonly HashSet<EntityUid> Avatars = new();
        public readonly List<EntityUid> Spawns = new();
        public List<EntityCoordinates> AttackerSpawns = new();
        public List<EntityCoordinates> DefenderSpawns = new();
        public readonly List<EntityUid> Goals = new();
        public readonly List<EntityUid> CacheSpawns = new();
        public readonly HashSet<EntityUid> Targets = new();
        public readonly List<EntityUid> Scenarios = new();
        public readonly HashSet<EntityUid> UsedScenarios = new();
        public readonly HashSet<EntityUid> Curiosities = new();
        public EntityUid? Cache;
        public int ObjectiveProgress;
        public bool StageOpened;
        public bool Completed;
        public TimeSpan? StopAt;
    }
}

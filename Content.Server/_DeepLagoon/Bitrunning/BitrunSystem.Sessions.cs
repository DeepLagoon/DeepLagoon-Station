using System.Linq;
using Content.Server.GameTicking;
using Content.Server.LandMines;
using Content.Server.Tiles;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.Chasm;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Physics;

namespace Content.Server._DeepLagoon.Bitrunning;

public sealed partial class BitrunSystem
{
    private const int TeamSize = 4;
    private const float DefenderSeparation = 5f;
    private static readonly TimeSpan RunDuration = TimeSpan.FromMinutes(15);
    private static readonly BitrunSide[] Teams = [BitrunSide.Attackers, BitrunSide.Defenders];

    [Dependency] private TurfSystem _turf = default!;
    [Dependency] private IConfigurationManager _configuration = default!;

    private readonly Dictionary<EntityUid, Lobby> _lobbies = new();
    private readonly Dictionary<EntityUid, Lobby> _queuedPods = new();
    private readonly Dictionary<NetUserId, EntityUid> _queuedUsers = new();
    private TimeSpan _lobbyEpoch;
    private TimeSpan _lobbyInterval;

    private sealed class Lobby(EntityUid server, EntityUid receiver, BitrunDomainPrototype domain,
        TimeSpan startAt, bool random)
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly EntityUid Server = server;
        public readonly EntityUid Receiver = receiver;
        public readonly BitrunDomainPrototype Domain = domain;
        public readonly bool Random = random;
        public readonly Dictionary<NetUserId, QueuedPlayer> Queued = new();
        public TimeSpan StartAt = startAt;
    }

    private sealed record QueuedPlayer(EntityUid Body, EntityUid Pod, BitrunSide Side);

    private sealed class Participant(BitrunSide side, EntityUid avatar)
    {
        public readonly BitrunSide Side = side;
        public EntityUid? Avatar = avatar;
        public bool Defeated;
    }

    private TimeSpan NextLobbyStart()
    {
        var elapsed = _timing.CurTime - _lobbyEpoch;
        return _lobbyEpoch + _lobbyInterval * (elapsed.Ticks / _lobbyInterval.Ticks + 1);
    }

    private void OnLobbyIntervalChanged(int seconds)
    {
        _lobbyInterval = TimeSpan.FromSeconds(Math.Max(1, seconds));
        _lobbyEpoch = _timing.CurTime;
        foreach (var lobby in _lobbies.Values)
            lobby.StartAt = _lobbyEpoch + _lobbyInterval;
    }

    private bool CanScheduleRun(EntityUid server, BitrunServerComponent machine, BitrunDomainPrototype domain, EntityUid receiver) =>
        _ticker.RunLevel != GameRunLevel.PostRound && !_runs.ContainsKey(server) && IsOnline(server) &&
        Transform(server).Anchored && CanUseResearch(server, receiver) && domain.Cost >= 0 && machine.Credits >= domain.Cost;

    private bool TryScheduleRun(EntityUid server, BitrunServerComponent machine, BitrunDomainPrototype domain,
        EntityUid receiver, bool random)
    {
        if (_lobbies.ContainsKey(server) || !CanScheduleRun(server, machine, domain, receiver))
            return false;

        _lobbies.Add(server, new Lobby(server, receiver, domain, NextLobbyStart(), random));
        _results.Remove(server);
        return true;
    }

    private bool CanJoinSession(EntityUid pod, EntityUid server)
    {
        if (!IsOnline(pod) || !IsOnline(server) || !HasComp<BitrunServerComponent>(server) ||
            !Transform(pod).Anchored || !Transform(server).Anchored)
            return false;

        var station = _stations.GetOwningStation(server);
        return station is { } owner
            ? _stations.GetOwningStation(pod) == owner
            : Transform(server).GridUid is { } grid && Transform(pod).GridUid == grid;
    }

    private static bool IsValidSide(BitrunSide side) => side is BitrunSide.Attackers or BitrunSide.Defenders;

    private bool CanEnroll(Lobby lobby, BitrunSide side, NetUserId? user = null) =>
        IsValidSide(side) && (user == null || !_queuedUsers.ContainsKey(user.Value)) &&
        lobby.Queued.Values.Count(player => player.Side == side) < TeamSize;

    private bool CanEnroll(Run run, BitrunSide side, NetUserId? user = null) =>
        IsValidSide(side) && !run.Completed && run.StopAt == null && _timing.CurTime < run.EndsAt &&
        (user == null || !_queuedUsers.ContainsKey(user.Value) && !run.Roster.ContainsKey(user.Value)) &&
        run.Roster.Values.Count(player => player.Side == side) < GetTeamSpawns(run, side).Count;

    private bool Enroll(Lobby lobby, EntityUid pod, EntityUid body, NetUserId user, BitrunSide side)
    {
        if (!CanEnroll(lobby, side, user) || !IsSeated(pod, body) && !SeatBody(pod, body))
            return false;

        _queuedPods.Add(pod, lobby);
        _queuedUsers.Add(user, pod);
        lobby.Queued.Add(user, new QueuedPlayer(body, pod, side));
        _appearance.SetData(pod, BitrunMachineVisuals.Occupied, true);
        return true;
    }

    private EntityUid? GetQueuedBody(EntityUid pod) =>
        _queuedPods.TryGetValue(pod, out var lobby)
            ? lobby.Queued.Values.FirstOrDefault(player => player.Pod == pod)?.Body
            : null;

    private void LeaveQueue(EntityUid pod)
    {
        if (!_queuedPods.TryGetValue(pod, out var lobby))
            return;

        foreach (var (user, player) in lobby.Queued)
        {
            if (player.Pod != pod)
                continue;

            RemoveEnrollment(lobby, user, player, releaseBody: true);
            return;
        }
    }

    private void RemoveEnrollment(Lobby lobby, NetUserId user, QueuedPlayer player, bool releaseBody)
    {
        lobby.Queued.Remove(user);
        _queuedPods.Remove(player.Pod);
        _queuedUsers.Remove(user);
        if (releaseBody)
            ReleaseBody(player.Pod, player.Body);
    }

    private void CancelLobby(Lobby lobby)
    {
        _lobbies.Remove(lobby.Server);
        foreach (var (user, player) in lobby.Queued.ToArray())
            RemoveEnrollment(lobby, user, player, releaseBody: true);
    }

    private void PollLobby(Lobby lobby)
    {
        if (!TryComp<BitrunServerComponent>(lobby.Server, out var server) ||
            !CanScheduleRun(lobby.Server, server, lobby.Domain, lobby.Receiver))
        {
            CancelLobby(lobby);
            if (!TerminatingOrDeleted(lobby.Server))
                _results[lobby.Server] = Loc.GetString("bitrun-start-failed");
            return;
        }

        foreach (var (user, player) in lobby.Queued.ToArray())
        {
            if (!IsSeated(player.Pod, player.Body) || !CanJoinSession(player.Pod, lobby.Server) ||
                !TryGetPodUser(player.Body, out _, out var currentUser) || currentUser != user)
                RemoveEnrollment(lobby, user, player, releaseBody: true);
        }

        if (_timing.CurTime < lobby.StartAt)
            return;

        if (!lobby.Queued.Values.Any(player => player.Side == BitrunSide.Attackers))
        {
            lobby.StartAt = NextLobbyStart();
            return;
        }

        if (!TryLaunch(lobby.Server, server, lobby.Domain, lobby.Receiver, lobby.Random, lobby.Id))
        {
            CancelLobby(lobby);
            _results[lobby.Server] = Loc.GetString("bitrun-start-failed");
            return;
        }

        var run = _runs[lobby.Server];
        _lobbies.Remove(lobby.Server);
        foreach (var side in Teams)
        {
            foreach (var (user, player) in lobby.Queued.Where(entry => entry.Value.Side == side).ToArray())
            {
                RemoveEnrollment(lobby, user, player, releaseBody: false);
                if (!TryComp<BitrunPodComponent>(player.Pod, out var pod) ||
                    !ConnectAvatar(player.Pod, pod, player.Body, run, side, user, alreadySeated: true))
                    ReleaseBody(player.Pod, player.Body);
            }

            if (side == BitrunSide.Attackers && GetSideCount(run, side) == 0)
            {
                CancelLobby(lobby);
                Finish(run);
                _results[lobby.Server] = Loc.GetString("bitrun-start-failed");
                return;
            }
        }

        server.Credits -= lobby.Domain.Cost;
        try
        {
            MaybeSpawnGlitch(run);
        }
        catch (Exception error)
        {
            Log.Warning($"Bitrun {run.Id} could not spawn an optional glitch: {error}");
        }
    }

    private static List<EntityCoordinates> GetTeamSpawns(Run run, BitrunSide side) =>
        side == BitrunSide.Attackers ? run.AttackerSpawns : run.DefenderSpawns;

    internal (List<EntityCoordinates> Attackers, List<EntityCoordinates> Defenders) FindTeamSpawns(
        Entity<MapGridComponent> grid, IEnumerable<EntityUid> attackerMarkers, IEnumerable<EntityUid> defenderMarkers)
    {
        var attackerAnchors = attackerMarkers.Select(uid => Transform(uid).Coordinates).ToArray();
        var defenderAnchors = defenderMarkers.Select(uid => Transform(uid).Coordinates).ToArray();
        var candidates = new List<(TileRef Tile, EntityCoordinates Coordinates, float Attacker, float Defender)>();
        foreach (var tile in _maps.GetAllTiles(grid, grid.Comp))
        {
            if (_turf.IsSpace(tile))
                continue;

            var coords = _maps.GridTileToLocal(grid, grid.Comp, tile.GridIndices);
            candidates.Add((tile, coords, DistanceToAnchors(coords, attackerAnchors), DistanceToAnchors(coords, defenderAnchors)));
        }

        var attackers = new List<EntityCoordinates>();
        foreach (var candidate in candidates.OrderBy(x => x.Attacker))
        {
            if (IsSpawnClear(candidate.Tile, grid.Comp))
                attackers.Add(candidate.Coordinates);

            if (attackers.Count == TeamSize)
                break;
        }

        var defenders = new List<EntityCoordinates>();
        foreach (var candidate in candidates.OrderBy(x => x.Attacker < DefenderSeparation).ThenBy(x => x.Defender))
        {
            if (!attackers.Contains(candidate.Coordinates) && IsSpawnClear(candidate.Tile, grid.Comp))
                defenders.Add(candidate.Coordinates);

            if (defenders.Count == TeamSize)
                break;
        }

        return (attackers, defenders);
    }

    private float DistanceToAnchors(EntityCoordinates coords, EntityCoordinates[] anchors) =>
        anchors.Min(anchor => coords.TryDistance(EntityManager, anchor, out var distance) ? distance : float.MaxValue);

    private bool IsSpawnClear(TileRef tile, MapGridComponent grid)
    {
        if (_turf.IsTileBlocked(tile, CollisionGroup.MobMask))
            return false;

        foreach (var entity in _maps.GetAnchoredEntities(tile.GridUid, grid, tile.GridIndices))
        {
            if (HasComp<ChasmComponent>(entity) || HasComp<TileEntityEffectComponent>(entity) || HasComp<LandMineComponent>(entity) ||
                TryComp<FixturesComponent>(entity, out var fixtures) &&
                fixtures.Fixtures.Values.Any(fixture => fixture.Hard && (fixture.CollisionLayer & (int) CollisionGroup.MobMask) != 0))
                return false;
        }

        return true;
    }

    private static int GetSideCount(Run run, BitrunSide side) =>
        run.Roster.Values.Count(player => player.Side == side && player.Avatar != null);

    private static IEnumerable<NetUserId> GetWinningUsers(Run run, BitrunSide side) =>
        run.Roster.Where(pair => pair.Value.Side == side && (pair.Value.Avatar != null || pair.Value.Defeated))
            .Select(pair => pair.Key);

    private bool ResolveTeams(Run run)
    {
        var attackers = GetSideCount(run, BitrunSide.Attackers);
        var defenders = GetSideCount(run, BitrunSide.Defenders);
        if (attackers == 0)
        {
            if (defenders > 0 && HasDefeatedPlayer(run, BitrunSide.Attackers))
                CompleteRun(run, BitrunSide.Defenders, elimination: true);
            else
                DrawRun(run);
            return true;
        }

        if (defenders == 0 && HasDefeatedPlayer(run, BitrunSide.Defenders))
        {
            CompleteRun(run, BitrunSide.Attackers, elimination: true);
            return true;
        }

        if (_timing.CurTime < run.EndsAt)
            return false;

        if (defenders > 0)
            CompleteRun(run, BitrunSide.Defenders, elimination: true);
        else
            DrawRun(run);

        return true;
    }

    private static bool HasDefeatedPlayer(Run run, BitrunSide side) =>
        run.Roster.Values.Any(player => player.Side == side && player.Defeated);
}

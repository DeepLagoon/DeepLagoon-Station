using System.Numerics;
using Content.Shared.Hands.Components;
using Robust.Server.GameObjects;
using Robust.Shared;
using Robust.Shared.Enums;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

public sealed partial class OrbitalDebrisSystem
{
    public readonly record struct Observer(EntityUid Map, Vector2 Position, float Range, Vector2 Velocity = default);
    private sealed class Viewer(ICommonSession session)
    {
        public readonly ICommonSession Session = session;
        public readonly List<Observer> Views = new();
        public int Generation;
        public readonly Dictionary<(EntityUid Map, SpaceJunkHabitatKind Kind), TimeSpan> NextSpawn = new();
    }
    private readonly Dictionary<ICommonSession, Viewer> _viewers = new();
    private readonly List<Viewer> _activeViewers = new();
    private readonly List<ICommonSession> _departed = new();
    private readonly List<Observer> _observers = new();
    private readonly HashSet<EntityUid> _eyes = new();
    private readonly HashSet<EntityUid> _occupied = new();
    private readonly HashSet<EntityUid> _viewMaps = new();
    private readonly List<(EntityUid Uid, float Distance)> _limitCandidates = new();
    private readonly Dictionary<EntityUid, EntityUid> _looseLoot = new();
    private readonly List<EntityUid> _lootSnapshot = new();
    private int _viewGeneration;

    public static bool Visible(Observer observer, EntityUid map, Vector2 position, float padding = 8f)
        => observer.Map == map && MathF.Abs(position.X - observer.Position.X) <= observer.Range + padding
            && MathF.Abs(position.Y - observer.Position.Y) <= observer.Range + padding;

    private bool SeenByAnyone(EntityUid map, Vector2 position, float padding = 8f)
        => _observers.Exists(view => Visible(view, map, position, padding));

    private void CollectObservers()
    {
        _viewGeneration++;
        _observers.Clear();
        _activeViewers.Clear();
        _occupied.Clear();
        foreach (var session in _players.Sessions)
        {
            if (session.Status != SessionStatus.InGame) continue;
            if (!_viewers.TryGetValue(session, out var viewer)) _viewers[session] = viewer = new Viewer(session);
            viewer.Views.Clear();
            viewer.Generation = _viewGeneration;
            _eyes.Clear();
            if (session.AttachedEntity is { } attached)
            {
                if (TryComp<TransformComponent>(attached, out var body) && body.GridUid is { } grid) _occupied.Add(grid);
                AddObserver(viewer, attached);
            }
            foreach (var eye in session.ViewSubscriptions) AddObserver(viewer, eye);
            if (viewer.Views.Count > 0) _activeViewers.Add(viewer);
        }
        _departed.Clear();
        foreach (var (session, viewer) in _viewers)
            if (viewer.Generation != _viewGeneration) _departed.Add(session);
        foreach (var session in _departed) _viewers.Remove(session);
    }

    private void AddObserver(Viewer viewer, EntityUid uid)
    {
        if (!_eyes.Add(uid) || !TryComp<TransformComponent>(uid, out var transform) || transform.MapUid is not { } map) return;
        var position = _transform.GetWorldPosition(transform);
        var range = MathF.Max(16f, MathF.Max(_cfg.GetCVar(CVars.NetMaxUpdateRange), _cfg.GetCVar(CVars.NetPvsPriorityRange)));
        if (TryComp<EyeComponent>(uid, out var eye))
        {
            position += eye.Offset;
            range *= eye.PvsScale;
        }
        var observer = new Observer(map, position, MathF.Max(range * 0.5f, 1f) + 32f, _physics.GetMapLinearVelocity(uid));
        viewer.Views.Add(observer);
        _observers.Add(observer);
    }

    private int Quota(Viewer viewer, EntityUid map)
    {
        var players = 0;
        var ordinal = 0;
        foreach (var other in _activeViewers)
        {
            if (!other.Views.Exists(view => view.Map == map)) continue;
            if (ReferenceEquals(viewer, other)) ordinal = players;
            players++;
        }
        // With more observers than slots, rotate the remainder so everyone gets a turn.
        if (players > MapLimit) ordinal = (ordinal + (int) (_timing.CurTime.TotalSeconds / 30) % players) % players;
        return FairShare(players, ordinal);
    }

    private bool CanAddAt(EntityUid map, Vector2 position)
    {
        foreach (var viewer in _activeViewers)
        {
            if (!viewer.Views.Exists(view => Visible(view, map, position))) continue;
            var count = 0;
            foreach (var (uid, pile) in _piles)
                if (viewer.Views.Exists(view => Visible(view, pile.Map, _transform.GetWorldPosition(uid)))) count++;
            if (count >= VisibleLimit) return false;
        }
        return true;
    }

    private void EnforceVisibleLimits()
    {
        if (_piles.Count <= VisibleLimit) return;
        foreach (var viewer in _activeViewers) EnforceViewLimit(viewer.Views);
    }

    /// <summary>One budget across all eyes of a player; overlapping players share the same physical population.</summary>
    public void EnforceViewLimit(IReadOnlyList<Observer> views)
    {
        _limitCandidates.Clear();
        foreach (var (uid, pile) in _piles)
        {
            if (TerminatingOrDeleted(uid)) continue;
            var position = _transform.GetWorldPosition(uid);
            var distance = float.PositiveInfinity;
            foreach (var view in views)
                if (Visible(view, pile.Map, position)) distance = MathF.Min(distance, Vector2.DistanceSquared(view.Position, position));
            if (!float.IsPositiveInfinity(distance)) _limitCandidates.Add((uid, distance));
        }
        _limitCandidates.Sort((a, b) => b.Distance.CompareTo(a.Distance));
        for (var i = 0; i < _limitCandidates.Count - VisibleLimit; i++) RetireSurplus(_limitCandidates[i].Uid);
    }

    private void RetireSurplus(EntityUid uid)
    {
        if (!_piles.TryGetValue(uid, out var pile)) return;
        if (TryComp<MapGridComponent>(uid, out var grid) && Claimed(uid, grid, pile)) Release(uid);
        else DeletePile(uid);
    }

    private void RebalanceShares()
    {
        foreach (var viewer in _activeViewers)
        {
            _viewMaps.Clear();
            foreach (var view in viewer.Views)
            {
                if (!_viewMaps.Add(view.Map)) continue;
                var quota = Quota(viewer, view.Map);
                _limitCandidates.Clear();
                foreach (var (uid, pile) in _piles)
                {
                    if (pile.Map != view.Map || pile.Sponsor != viewer.Session) continue;
                    var position = _transform.GetWorldPosition(uid);
                    _limitCandidates.Add((uid, SeenByAnyone(pile.Map, position)
                        ? Vector2.DistanceSquared(view.Position, position) : float.MaxValue));
                }
                _limitCandidates.Sort((a, b) => b.Distance.CompareTo(a.Distance));
                for (var i = 0; i < _limitCandidates.Count - quota; i++) RetireSurplus(_limitCandidates[i].Uid);
            }
        }
    }

    private void CleanDetachedLoot()
    {
        _lootSnapshot.Clear();
        _lootSnapshot.AddRange(_looseLoot.Keys);
        foreach (var item in _lootSnapshot)
        {
            if (TerminatingOrDeleted(item) || !TryComp<TransformComponent>(item, out var transform))
            {
                _looseLoot.Remove(item);
                continue;
            }
            var home = _looseLoot[item];
            if (transform.GridUid == home) continue;
            if (Carried(item) || transform.GridUid is { } other && !_piles.ContainsKey(other))
            {
                // Taking a find into inventory or onto an ordinary ship makes it player property.
                _looseLoot.Remove(item);
                continue;
            }
            if (transform.MapUid is { } map && SeenByAnyone(map, _transform.GetWorldPosition(transform), -24f)) continue;
            _looseLoot.Remove(item);
            QueueDel(item);
        }
    }

    private bool Carried(EntityUid uid)
    {
        if (HasComp<ActorComponent>(uid)) return true;
        var current = Transform(uid).ParentUid;
        for (var depth = 0; depth < 32 && TryComp<TransformComponent>(current, out var transform); depth++)
        {
            if (HasComp<ActorComponent>(current) || HasComp<HandsComponent>(current)) return true;
            if (transform.ParentUid == EntityUid.Invalid) break;
            current = transform.ParentUid;
        }
        return false;
    }
}

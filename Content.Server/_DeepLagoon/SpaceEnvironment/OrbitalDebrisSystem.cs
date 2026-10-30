using System.Numerics;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared.EntityTable;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

/// <summary>Bounded physical space junk, sampled near observers and away from inhabited locations.</summary>
public sealed partial class OrbitalDebrisSystem : EntitySystem
{
    public const int MapLimit = 100;
    public const int VisibleLimit = 10;
    public const float UnseenSeconds = 10;
    public const string SupportTile = "Plating";
    public const string LootTable = "DeepLagoonSpaceJunkLoot";
    [Dependency] private readonly MapSystem _maps = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ITileDefinitionManager _tiles = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly EntityTableSystem _tables = default!;
    private readonly Dictionary<EntityUid, Pile> _piles = new();
    private readonly List<EntityUid> _snapshot = new();
    private readonly Dictionary<(EntityUid Map, Vector2i Cell), TimeSpan> _cooldowns = new();
    private readonly List<(EntityUid Map, Vector2i Cell)> _expired = new();
    private TimeSpan _next;
    private uint _sequence = 1;
    private int _playerCursor;
    public int ActiveCount => _piles.Count;

    private sealed class Pile
    {
        public EntityUid Map;
        public Vector2 Anchor;
        public float Radius;
        public float Speed;
        public float Direction;
        public TimeSpan LastSeen;
        public TimeSpan InterceptUntil;
        public ICommonSession? Sponsor;
        public readonly Dictionary<Vector2i, Tile> Tiles = new();
        public readonly HashSet<EntityUid> Children = new();
    }

    public override void Shutdown()
    {
        _piles.Clear();
        _cooldowns.Clear();
        _looseLoot.Clear();
        _viewers.Clear();
        _protected.Clear();
        base.Shutdown();
    }

    public static int DesiredCount(float distanceToHabitation)
        => 2 + (int) Math.Clamp(distanceToHabitation / 4000f, 0f, 8f);

    public static int FairShare(int players, int ordinal)
        => players <= 0 ? VisibleLimit : Math.Min(VisibleLimit, MapLimit / players + (ordinal < MapLimit % players ? 1 : 0));

    public override void Update(float frameTime)
    {
        CollectObservers();
        // Visibility caps and detached loot cleanup follow movement on each simulation update.
        EnforceVisibleLimits();
        CleanDetachedLoot();
        if (_timing.CurTime < _next) return;
        _next = _timing.CurTime + TimeSpan.FromSeconds(1);
        RefreshExclusions();
        Snapshot();
        foreach (var uid in _snapshot)
        {
            if (!TryComp<MapGridComponent>(uid, out var grid) || TerminatingOrDeleted(uid))
            {
                _piles.Remove(uid);
                continue;
            }
            var pile = _piles[uid];
            if (Claimed(uid, grid, pile)) { Release(uid); continue; }
            var position = _transform.GetWorldPosition(uid);
            TryComp<PhysicsComponent>(uid, out var body);
            if (!OrbitClear(pile.Map, pile.Anchor, pile.Radius) || !PositionClear(pile.Map, position)
                || body != null && !PathClear(pile.Map, position, position + body.LinearVelocity * 3f))
            {
                DeletePile(uid);
                continue;
            }
            if (SeenByAnyone(pile.Map, position)) pile.LastSeen = _timing.CurTime;
            else if (_timing.CurTime >= pile.InterceptUntil && (_timing.CurTime - pile.LastSeen).TotalSeconds >= UnseenSeconds)
            {
                DeletePile(uid);
                continue;
            }
            if (body == null) continue;
            var radial = position - pile.Anchor;
            var distance = radial.Length();
            if (distance < 1f) continue;
            var normal = radial / distance;
            var tangent = new Vector2(-normal.Y, normal.X) * pile.Direction;
            var desired = tangent * pile.Speed + normal * Math.Clamp((pile.Radius - distance) * 0.1f, -2f, 2f);
            _physics.SetLinearVelocity(uid, Vector2.Lerp(body.LinearVelocity, desired, 0.3f), body: body);
        }
        _expired.Clear();
        foreach (var (cell, expiry) in _cooldowns)
            if (expiry <= _timing.CurTime || TerminatingOrDeleted(cell.Map)) _expired.Add(cell);
        foreach (var cell in _expired) _cooldowns.Remove(cell);
        if (!_cfg.GetCVar(SpaceEnvironmentCVars.Enabled) || !_cfg.GetCVar(SpaceEnvironmentCVars.DebrisEnabled)) return;
        RebalanceShares();
        var budget = 2;
        for (var offset = 0; offset < _activeViewers.Count && budget > 0; offset++)
        {
            var viewer = _activeViewers[(_playerCursor + offset) % _activeViewers.Count];
            foreach (var observer in viewer.Views)
            {
                if (!TrySpawnFor(viewer, observer)) continue;
                budget--;
                break;
            }
        }
        if (_activeViewers.Count > 0) _playerCursor = (_playerCursor + 1) % _activeViewers.Count;
    }

    private bool TrySpawnFor(Viewer viewer, Observer observer)
    {
        if (!TryComp<SpaceEnvironmentComponent>(observer.Map, out var environment)) return false;
        var quota = Quota(viewer, observer.Map);
        var nearby = 0;
        var owned = 0;
        var inMap = 0;
        foreach (var (uid, pile) in _piles)
        {
            if (pile.Map != observer.Map) continue;
            inMap++;
            if (pile.Sponsor == viewer.Session) owned++;
            if (Visible(observer, pile.Map, _transform.GetWorldPosition(uid), 100f)) nearby++;
        }
        if (inMap >= MapLimit || owned >= quota || nearby >= quota) return false;
        var random = new SectorRandom(SectorRandom.Derive(environment.Definition.Seed, $"debris/{_sequence++}"));
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (observer.Velocity.LengthSquared() > 500f * 500f) return false;
            var seed = random.Next();
            var lateral = random.Range(-1f, 1f);
            var extra = random.Range(0, 3f);
            var angle = random.Range(0, MathF.Tau);
            var plan = PlanEncounter(environment.Definition, observer, seed, lateral, extra, angle, attempt % 2 != 0);
            var position = plan.Position;
            if (observer.Velocity.LengthSquared() > 16f)
            {
                var speed = observer.Velocity.Length();
                var lead = Vector2.Dot(position - observer.Position, observer.Velocity / speed);
                if (lead < observer.Range + (speed + 8f) * ReactionSeconds + 16f) continue;
            }
            var habitat = Habitat(environment.Definition, position);
            var target = habitat.Rich ? Math.Min(habitat.Target, Math.Max(6, DesiredCount(HabitationDistance(observer)))) : habitat.Target;
            if (nearby >= Math.Min(quota, target)) continue;
            var cooldown = (observer.Map, habitat.Kind);
            if (!viewer.NextSpawn.TryGetValue(cooldown, out var ready))
            {
                ready = _timing.CurTime + TimeSpan.FromSeconds(habitat.Kind == SpaceJunkHabitatKind.OpenSpace ? random.Range(30, 90) : 0);
                viewer.NextSpawn[cooldown] = ready;
            }
            if (_timing.CurTime < ready) continue;
            if (_observers.Exists(view => Visible(view, observer.Map, position, -16f)) || NearGrid(observer.Map, position)
                || !CanAddAt(observer.Map, position)) continue;
            var anchor = plan.Anchor;
            if (!OrbitClear(observer.Map, anchor, Vector2.Distance(position, anchor))) continue;
            var cell = (observer.Map, new Vector2i((int) MathF.Floor(position.X / 32), (int) MathF.Floor(position.Y / 32)));
            if (_cooldowns.ContainsKey(cell) || _cooldowns.Count >= 4096) continue;
            var spawned = SpawnPile(observer.Map, position, anchor, seed);
            if (spawned is not { } uid) continue;
            var pile = _piles[uid];
            pile.Sponsor = viewer.Session;
            pile.InterceptUntil = _timing.CurTime + TimeSpan.FromSeconds(MathF.Min(90f, plan.ArrivalSeconds + 5f));
            viewer.NextSpawn[cooldown] = _timing.CurTime + TimeSpan.FromSeconds(habitat.Interval);
            pile.Direction = plan.Direction;
            var radial = Normal(position - anchor);
            _physics.SetLinearVelocity(uid, new Vector2(-radial.Y, radial.X) * (plan.Speed * plan.Direction));
            _cooldowns[cell] = _timing.CurTime + TimeSpan.FromSeconds(120);
            return true;
        }
        return false;
    }

    public static Vector2 ClosestAnchor(SectorDefinition sector, Vector2 position)
    {
        var closest = Vector2.Zero;
        var distance = float.PositiveInfinity;
        foreach (var planet in sector.Planets) Consider(new Vector2(planet.X, planet.Y));
        foreach (var region in sector.Regions) Consider(new Vector2(region.X, region.Y));
        return closest;
        void Consider(Vector2 candidate)
        {
            var squared = Vector2.DistanceSquared(position, candidate);
            if (squared >= distance) return;
            distance = squared;
            closest = candidate;
        }
    }

    /// <summary>Native scrap walls on standard plating, with bounded field-specific size and contents.</summary>
    public EntityUid? SpawnPile(EntityUid map, Vector2 position, Vector2 anchor, uint seed)
    {
        if (!TryComp<MapComponent>(map, out var mapComponent) || !float.IsFinite(position.X) || !float.IsFinite(position.Y)
            || !float.IsFinite(anchor.X) || !float.IsFinite(anchor.Y)) return null;
        RefreshExclusions();
        if (!PositionClear(map, position) || !OrbitClear(map, anchor, Vector2.Distance(position, anchor)) || !CanAddAt(map, position)) return null;
        var count = 0;
        foreach (var pile in _piles.Values) if (pile.Map == map) count++;
        if (count >= MapLimit) return null;
        var random = new SectorRandom(seed);
        var grid = _maps.CreateGridEntity(mapComponent.MapId);
        _transform.SetWorldPosition(grid, position);
        var state = new Pile { Map = map, Anchor = anchor, Radius = MathF.Max(1f, Vector2.Distance(position, anchor)),
            Speed = random.Range(2f, 6f), Direction = random.Index(2) == 0 ? -1f : 1f, LastSeen = _timing.CurTime };
        var habitat = TryComp<SpaceEnvironmentComponent>(map, out var environment)
            ? Habitat(environment.Definition, position) : new SpaceJunkHabitat(SpaceJunkHabitatKind.OpenSpace, 1, 4, 16, 90, false);
        var width = 1 + random.Index(habitat.MaxExtent);
        var height = 1 + random.Index(habitat.MaxExtent);
        var floor = new Tile(_tiles[SupportTile].TileId);
        var cells = new List<(Vector2i GridIndices, Tile Tile)>();
        foreach (var index in JunkShape(width, height, habitat.TileBudget, ref random))
        {
            cells.Add((index, floor));
            state.Tiles.Add(index, floor);
        }
        _maps.SetTiles(grid, cells);
        var pocket = cells.Count > 1 ? cells[0].GridIndices : (Vector2i?) null;
        foreach (var (index, _) in cells)
        {
            if (index == pocket) continue;
            Spawn(ScrapWall(ref random, habitat.Rich), new EntityCoordinates(grid, (Vector2) index + new Vector2(0.5f)));
        }
        var lootPosition = new EntityCoordinates(grid, (Vector2) (pocket ?? cells[0].GridIndices) + new Vector2(0.5f));
        var lootRandom = new System.Random(unchecked((int) random.Next()));
        var maximumLoot = habitat.Rich ? 4 : 2;
        var lootCount = 0;
        var tableId = habitat.Kind == SpaceJunkHabitatKind.AsteroidField ? "DeepLagoonSpaceJunkAsteroidLoot"
            : habitat.Rich ? "DeepLagoonSpaceJunkFieldLoot" : LootTable;
        var table = _prototypes.Index<EntityTablePrototype>(tableId).Table;
        for (var roll = 0; roll < (habitat.Rich ? 2 : 1) && lootCount < maximumLoot; roll++)
        foreach (var proto in _tables.GetSpawns(table, lootRandom))
        {
            Spawn(proto, lootPosition);
            if (++lootCount >= maximumLoot) break;
        }
        if (habitat.Rich && random.Index(8) == 0) Spawn("MobCarpSalvage", lootPosition);
        // Native table spawners finish during MapInit. Capture their real outputs, not only marker UIDs.
        var children = Transform(grid).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            state.Children.Add(child);
            if (!Transform(child).Anchored && !TerminatingOrDeleted(child)) _looseLoot[child] = grid;
        }
        var body = EnsureComp<PhysicsComponent>(grid);
        _physics.SetBodyType(grid, BodyType.Dynamic, body: body);
        var radial = Normal(position - anchor);
        _physics.SetLinearVelocity(grid, new Vector2(-radial.Y, radial.X) * (state.Speed * state.Direction), body: body);
        _physics.SetAngularVelocity(grid, random.Range(-0.08f, 0.08f), body: body);
        EntityManager.System<MetaDataSystem>().SetEntityName(grid, Loc.GetString("space-environment-space-junk"));
        _piles.Add(grid, state);
        return grid;
    }

    private static Vector2 Normal(Vector2 offset) => offset.LengthSquared() > 0.0001f ? Vector2.Normalize(offset) : Vector2.UnitX;
    private void Snapshot() { _snapshot.Clear(); _snapshot.AddRange(_piles.Keys); }
    private void DeletePile(EntityUid uid) { _piles.Remove(uid); QueueDel(uid); }
    private void Release(EntityUid uid)
    {
        _piles.Remove(uid);
        _lootSnapshot.Clear();
        foreach (var (item, home) in _looseLoot)
            if (home == uid && (!TryComp<TransformComponent>(item, out var transform)
                || transform.GridUid == uid || Carried(item))) _lootSnapshot.Add(item);
        foreach (var item in _lootSnapshot) _looseLoot.Remove(item);
    }

    private bool Claimed(EntityUid uid, MapGridComponent grid, Pile pile)
    {
        var transform = Transform(uid);
        if (transform.MapUid != pile.Map || _occupied.Contains(uid)) return true;
        var children = transform.ChildEnumerator;
        while (children.MoveNext(out var child)) if (!pile.Children.Contains(child)) return true;
        var tiles = _maps.GetAllTiles(uid, grid);
        while (tiles.MoveNext())
        {
            var tile = tiles.Current;
            if (!pile.Tiles.TryGetValue(tile.GridIndices, out var original) || tile.Tile.TypeId != original.TypeId) return true;
        }
        return false;
    }

    public void ForgetMap(EntityUid map)
    {
        Snapshot();
        foreach (var uid in _snapshot) if (_piles[uid].Map == map) _piles.Remove(uid);
        foreach (var viewer in _viewers.Values)
            foreach (var kind in Enum.GetValues<SpaceJunkHabitatKind>()) viewer.NextSpawn.Remove((map, kind));
        _expired.Clear();
        foreach (var cell in _cooldowns.Keys) if (cell.Map == map) _expired.Add(cell);
        foreach (var cell in _expired) _cooldowns.Remove(cell);
    }

    public void ClearUnclaimed(EntityUid map)
    {
        CollectObservers();
        Snapshot();
        foreach (var uid in _snapshot)
        {
            var pile = _piles[uid];
            if (pile.Map != map) continue;
            if (TryComp<MapGridComponent>(uid, out var grid) && !Claimed(uid, grid, pile))
            {
                if (SeenByAnyone(map, _transform.GetWorldPosition(uid))) continue;
                _piles.Remove(uid);
                Del(uid);
            }
            else Release(uid);
        }
        _expired.Clear();
        foreach (var cell in _cooldowns.Keys) if (cell.Map == map) _expired.Add(cell);
        foreach (var cell in _expired) _cooldowns.Remove(cell);
    }
}

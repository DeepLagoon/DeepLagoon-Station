using System.Numerics;
using Content.Server.Emp;
using Content.Server.Power.Components;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared.Damage;
using Content.Shared.Maps;
using Content.Shared.Projectiles;
using Content.Shared.Tag;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Random;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

[RegisterComponent]
public sealed partial class SpaceDustProjectileComponent : Component
{
    public EntityUid Map;
    public string Region = "";
    public SpaceRegion? Boundary;
    public SectorDefinition? Definition;
}

public sealed partial class SpaceEnvironmentSystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedMapSystem _maps = default!;
    [Dependency] private readonly TurfSystem _turf = default!;
    [Dependency] private readonly TagSystem _tags = default!;
    [Dependency] private readonly EmpSystem _emp = default!;
    private readonly HashSet<EntityUid> _occupiedShuttles = new();
    private int _dustBudget;
    private int _empBudget;

    private void CollectOccupiedShuttles()
    {
        _occupiedShuttles.Clear();
        var actors = EntityQueryEnumerator<ActorComponent, TransformComponent>();
        while (actors.MoveNext(out _, out _, out var transform))
            if (transform.GridUid is { } grid)
                _occupiedShuttles.Add(grid);
    }

    private void TrySpawnDust(EntityUid ship, EntityUid map, TransformComponent transform, ShipState state, SpaceRegion region)
    {
        if (_dustBudget < 2 || _timing.CurTime < state.NextDust ||
            !TryComp<MapGridComponent>(ship, out var grid) || !TryComp<PhysicsComponent>(ship, out var body))
            return;

        var center = _transform.ToMapCoordinates(new EntityCoordinates(ship, grid.LocalAABB.Center)).Position;
        var velocity = _physics.GetMapLinearVelocity(ship, body, transform);
        var radius = grid.LocalAABB.Size.Length() * 0.5f;
        var direction = _random.NextAngle().RotateVec(Vector2.UnitX);
        var across = new Vector2(-direction.Y, direction.X);
        var distance = MathF.Max(20, radius + 12);
        var velocityOfDust = direction * MathF.Max(45, distance / 1.5f) + velocity;
        var corridor = NavigationSiteCatalog.InDustCorridor(region, center, center);
        state.NextDust = _timing.CurTime + TimeSpan.FromSeconds(_random.NextFloat(3, 6) * (corridor ? 4 : 1));

        // One particle crosses the projected hull and a second passes just outside it.
        for (var i = 0; i < 2; i++)
        {
            var offset = i == 0 ? _random.NextFloat(-0.35f, 0.35f) * radius : (radius + 3) * (_random.Prob(0.5f) ? 1 : -1);
            var start = center + across * offset - direction * distance;
            if (!SpaceRegionGeometry.Contains(region, start))
                continue;

            var coordinates = new MapCoordinates(start, transform.MapID);
            if (_maps.TryFindGridAt(coordinates, out _, out _))
                continue;

            var uid = Spawn("DeepLagoonSpaceDustProjectile", coordinates);
            var dust = Comp<SpaceDustProjectileComponent>(uid);
            dust.Map = map;
            dust.Region = region.Id;
            _transform.SetWorldRotation(uid, velocityOfDust.ToAngle());
            _physics.SetLinearVelocity(uid, velocityOfDust);
            _dustBudget--;
        }
    }

    private void KeepDustInRegion(float frameTime)
    {
        var query = EntityQueryEnumerator<SpaceDustProjectileComponent, ProjectileComponent, PhysicsComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var dust, out var projectile, out var body, out var transform))
        {
            var position = _transform.GetWorldPosition(transform);
            var next = position + _physics.GetMapLinearVelocity(uid, body, transform) * frameTime;
            if (transform.MapUid == dust.Map && IsDustInside(dust, position) && IsDustInside(dust, next))
                continue;

            projectile.ProjectileSpent = true;
            QueueDel(uid);
        }
    }

    private bool IsDustInside(SpaceDustProjectileComponent dust, Vector2 position)
    {
        if (!_sectors.Enabled || !TryComp<SpaceEnvironmentComponent>(dust.Map, out var environment))
            return false;

        if (!ReferenceEquals(dust.Definition, environment.Definition))
        {
            dust.Definition = environment.Definition;
            dust.Boundary = environment.Definition.Regions.Find(region => region.Id == dust.Region && region.Kind == SpaceRegionKind.Dust);
        }
        return dust.Boundary is { } boundary && SpaceRegionGeometry.Contains(boundary, position);
    }

    private void OnDustHit(EntityUid uid, SpaceDustProjectileComponent dust, ref ProjectileHitEvent args)
    {
        // Consume the particle on the first solid object, even if it cannot harm it.
        args.Handled = true;
        Comp<ProjectileComponent>(uid).ProjectileSpent = true;
        QueueDel(uid);
        if (Transform(args.Target).MapUid != dust.Map ||
            Transform(uid).MapUid != dust.Map ||
            !TryComp<DamageableComponent>(args.Target, out var target) ||
            target.DamageModifierSetId?.Id != "Glass" || !_tags.HasTag(args.Target, "Window") ||
            !IsDustInside(dust, _transform.GetWorldPosition(uid)) ||
            !IsDustInside(dust, _transform.GetWorldPosition(args.Target)))
            return;

        _damage.TryChangeDamage(args.Target, args.Damage, tool: uid);
    }

    private void TryPlasmaEmp(EntityUid ship, SpaceEnvironmentComponent environment, ShipState state)
    {
        if (_empBudget == 0 || _timing.CurTime < state.NextEmp || !TryComp<MapGridComponent>(ship, out var grid))
            return;

        state.NextEmp = _timing.CurTime + TimeSpan.FromSeconds(_random.NextFloat(100, 220));
        if (!_random.Prob(0.4f))
            return;

        var children = Transform(ship).ChildEnumerator;
        var inspected = 0;
        var eligible = 0;
        EntityUid? selected = null;
        while (children.MoveNext(out var child) && inspected++ < 1024)
        {
            var transform = Transform(child);
            if (!transform.Anchored || !TryComp<ApcPowerReceiverComponent>(child, out var receiver) || !receiver.NeedsPower || !receiver.Powered ||
                !TouchesSpace(ship, grid, transform.Coordinates))
                continue;

            var position = _transform.GetWorldPosition(transform);
            if (!environment.Definition.Regions.Exists(region => region.Kind == SpaceRegionKind.Plasma &&
                    SpaceRegionGeometry.Contains(region, position) &&
                    region.Intensity * PlasmaWeather.Strength(PlasmaWeather.Seed(environment.Definition.Seed, region.Id),
                        _timing.CurTime.TotalSeconds) >= 0.75f))
                continue;

            if (_random.Next(++eligible) == 0)
                selected = child;
        }

        if (selected is not { } target)
            return;

        _empBudget--;
        _emp.TryEmpEffects(target, 50, TimeSpan.FromSeconds(3));
    }

    private bool TouchesSpace(EntityUid ship, MapGridComponent grid, EntityCoordinates coordinates)
    {
        var cell = _maps.GetTileRef(ship, grid, coordinates).GridIndices;
        return _turf.IsSpace(_maps.GetTileRef(ship, grid, cell + new Vector2i(1, 0))) ||
            _turf.IsSpace(_maps.GetTileRef(ship, grid, cell + new Vector2i(-1, 0))) ||
            _turf.IsSpace(_maps.GetTileRef(ship, grid, cell + new Vector2i(0, 1))) ||
            _turf.IsSpace(_maps.GetTileRef(ship, grid, cell + new Vector2i(0, -1)));
    }
}

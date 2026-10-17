using Content.Shared._Exodus.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Threading;
using System.Linq;
using System.Numerics;
using Content.Server.Destructible;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Projectiles;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics; // Mono;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;

namespace Content.Server.Projectiles;

public sealed partial class ProjectileSystem : SharedProjectileSystem
{
    [Dependency] private DestructibleSystem _destructibleSystem = default!;

    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedTransformSystem _transformSystem = default!;

    // <Mono>
    private EntityQuery<PhysicsComponent> _physQuery;
    private EntityQuery<FixturesComponent> _fixQuery;


    public override void Initialize()
    {
        base.Initialize();

        // Mono
        _physQuery = GetEntityQuery<PhysicsComponent>();
        _fixQuery = GetEntityQuery<FixturesComponent>();
        // Mono
        UpdatesBefore.Add(typeof(SharedPhysicsSystem));
    }

    public override DamageSpecifier? ProjectileCollide(Entity<ProjectileComponent, PhysicsComponent> projectile, EntityUid target, MapCoordinates? collisionCoordinates, bool predicted = false)
    {
        var (uid, component, ourBody) = projectile;
        // Check if projectile is already spent (server-specific check)
        if (component.ProjectileSpent)
            return null;

        var otherName = ToPrettyString(target);
        // Get damage required for destructible before base applies damage
        var damageRequired = FixedPoint2.Zero;
        if (TryComp<DamageableComponent>(target, out var damageableComponent))
        {
            damageRequired = _destructibleSystem.DestroyedAt(target);
            damageRequired -= damageableComponent.TotalDamage;
            damageRequired = FixedPoint2.Max(damageRequired, FixedPoint2.Zero);
        }
        // var deleted = Deleted(target); // Mono: Unused

        // Call base implementation to handle damage application and other effects
        var modifiedDamage = base.ProjectileCollide(projectile, target, collisionCoordinates, predicted);

        if (modifiedDamage == null)
        {
            // mono start
            if (!component.NoDamageDelete)
                return null;

            var spEv = new ProjectileSpentEvent();
            RaiseLocalEvent(uid, spEv);
            // mono end

            component.ProjectileSpent = true;
            if (component.DeleteOnCollide)
                QueueDel(uid);
            return null;
        }

        // Server-specific logic: penetration
        if (component.PenetrationThreshold != 0)
        {
            // If a damage type is required, stop the bullet if the hit entity doesn't have that type.
            if (component.PenetrationDamageTypeRequirement != null)
            {
                var stopPenetration = false;
                foreach (var requiredDamageType in component.PenetrationDamageTypeRequirement)
                {
                    if (!modifiedDamage.DamageDict.Keys.Contains(requiredDamageType))
                    {
                        stopPenetration = true;
                        break;
                    }
                }

                if (stopPenetration)
                    component.ProjectileSpent = true;
            }

            // If the object won't be destroyed, it "tanks" the penetration hit.
            if (modifiedDamage.GetTotal() < damageRequired)
            {
                component.ProjectileSpent = true;
            }

            if (!component.ProjectileSpent)
            {
                component.PenetrationAmount += damageRequired;
                // The projectile has dealt enough damage to be spent.
                if (component.PenetrationAmount >= component.PenetrationThreshold)
                {
                    component.ProjectileSpent = true;
                }
            }
        }
        else
        {
            component.ProjectileSpent = true;
        }

        // Mono
        if (component.ProjectileSpent)
        {
            var spEv = new ProjectileSpentEvent();
            RaiseLocalEvent(uid, spEv);
            if (component.DeleteOnCollide)
                QueueDel(uid);
        }

        return modifiedDamage;
    }

    // Exodus-OptimizeProjectiles-Start
    private readonly List<(EntityUid Uid, ProjectileComponent Comp, PhysicsComponent Body, TransformComponent Xform, Fixture ProjFix)>
                  _projectiles = [];
    private ProjectileRayData[] _rayData = Array.Empty<ProjectileRayData>();
    private readonly HashSet<MapId> _rayMaps = new();
    [Dependency] private IParallelManager _rayParallel = default!;
    [Dependency] private IConfigurationManager _rayConfig = default!;

    private readonly record struct ProjectileRayJob(ProjectileSystem System, ProjectileRayData[] Rays) : IParallelRobustJob
    {
        public int BatchSize => 16;
        public void Execute(int index)
        {
            ref var data = ref Rays[index];
            // The official engine returns distance-sorted ray hits. Collision events are applied later on the main thread.
            data.Hits = System._physics.IntersectRay(data.MapId, data.Ray, data.MaxDistance, data.Ignore, false);
        }
    }

    private struct ProjectileRayData
    {
        public EntityUid Uid;
        public MapId MapId;
        public CollisionRay Ray;
        public float MaxDistance;
        public EntityUid Ignore;
        public IEnumerable<RayCastResults> Hits;
        public Vector2 Velocity;
    }
    // Exodus-OptimizeProjectiles-End

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Exodus-OptimizeProjectiles-Start: Gather all projectiles and raycast data in parallel, then process hits sequentially
        _projectiles.Clear();
        _rayMaps.Clear();

        var query = EntityQueryEnumerator<ProjectileComponent, PhysicsComponent>();

        while (query.MoveNext(out var uid, out var comp, out var body))
        {
            if (comp.ProjectileSpent || TerminatingOrDeleted(uid)
                || comp is { Weapon: null, OnlyCollideWhenShot: true })
                continue;

            var xform = Transform(uid);
            var vel = comp.RaycastResetVelocity ?? _physics.GetMapLinearVelocity(uid, body, xform);
            var velLen = vel.Length();
            if (!ShouldRaycastProjectile(velLen) && comp.RaycastResetVelocity == null)
                continue;

            var rayDistance = velLen * frameTime;
            if (!float.IsFinite(rayDistance) || rayDistance <= 0f || velLen <= float.Epsilon)
                continue;

            if (!_fixQuery.TryComp(uid, out var fix) || !fix.Fixtures.TryGetValue(ProjectileFixture, out var projFix))
                continue;

            _projectiles.Add((uid, comp, body, xform, projFix));
        }

        if (_rayData.Length < _projectiles.Count)
            Array.Resize(ref _rayData, _projectiles.Count);

        for (var i = 0; i < _projectiles.Count; i++)
        {
            var (uid, comp, body, xform, projFix) = _projectiles[i];
            var vel = comp.RaycastResetVelocity ?? _physics.GetMapLinearVelocity(uid, body, xform);
            var velLen = vel.Length();
            var rayDir = vel / velLen;
            var lastMap = _transformSystem.GetMapCoordinates(xform);
            var rayDist = velLen * frameTime;

            _rayMaps.Add(xform.MapID);
            _rayData[i] = new ProjectileRayData
            {
                Uid = uid,
                MapId = xform.MapID,
                Ray = new CollisionRay(lastMap.Position, rayDir, projFix.CollisionMask),
                MaxDistance = rayDist,
                Ignore = uid,
                Hits = [],
                Velocity = vel,
            };
        }

        var job = new ProjectileRayJob(this, _rayData);
        if (_projectiles.Count >= 32 && _rayConfig.GetCVar(EXCVars.ParallelProjectileRaycasts))
        {
            // Materialize lazy transform caches before workers traverse the read-only broadphase trees.
            // No entity mutation or collision event runs until ProcessNow joins all jobs.
            var broadphases = EntityQueryEnumerator<BroadphaseComponent, TransformComponent>();
            while (broadphases.MoveNext(out var broadphaseUid, out _, out var broadphaseTransform))
            {
                if (_rayMaps.Contains(broadphaseTransform.MapID))
                    _transformSystem.GetWorldPositionRotationMatrixWithInv(broadphaseUid);
            }
            _rayParallel.ProcessNow(job, _projectiles.Count);
        }
        else
        {
            for (var i = 0; i < _projectiles.Count; i++)
                job.Execute(i);
        }

        for (var i = 0; i < _projectiles.Count; i++)
        {
            var (uid, comp, body, xform, projFix) = _projectiles[i];
            var data = _rayData[i];
            _rayData[i] = default;
            if (TerminatingOrDeleted(uid))
                continue;

            if (!ProcessHitsSequential(uid, comp, body, xform, projFix, data.Hits, data.Velocity, frameTime))
            {
                // no valid hit вЂ“ reset velocity if needed
                if (comp.RaycastResetVelocity is { } resetVel)
                {
                    var parentVel = _physics.GetMapLinearVelocity(xform.ParentUid);
                    var resetTo = resetVel - parentVel;
                    _physics.SetLinearVelocity(uid, resetTo, body: body);
                    comp.RaycastResetVelocity = null;
                }
            }

            bool ProcessHitsSequential(EntityUid uid, ProjectileComponent comp, PhysicsComponent body,
                TransformComponent xform, Fixture projFix, IEnumerable<RayCastResults> sortedHits, Vector2 velocity, float frameTime)
            {
                foreach (var hit in sortedHits)
                {
                    var hitEnt = hit.HitEntity;
                    if (TerminatingOrDeleted(hitEnt)) continue;

                    if (!_physQuery.TryComp(hitEnt, out var otherBody) || !_fixQuery.TryComp(hitEnt, out var otherFix))
                        continue;

                    Fixture? hitFix = null;
                    foreach (var kv in otherFix.Fixtures)
                    {
                        if (kv.Value.Hard)
                        {
                            hitFix = kv.Value;
                            break;
                        }
                    }
                    if (hitFix == null) continue;

                    var ourEv = new PreventCollideEvent(uid, hitEnt, body, otherBody, projFix, hitFix);
                    RaiseLocalEvent(uid, ref ourEv);
                    if (ourEv.Cancelled) continue;

                    var otherEv = new PreventCollideEvent(hitEnt, uid, otherBody, body, hitFix, projFix);
                    RaiseLocalEvent(hitEnt, ref otherEv);
                    if (otherEv.Cancelled) continue;

                    // Valid hit вЂ“ apply it
                    var lastMap = _transformSystem.GetMapCoordinates(xform);
                    var hitMapCoord = lastMap.Offset(hit.Distance * data.Ray.Direction);
                    var hitPos = _transformSystem.ToCoordinates(hitMapCoord);
                    var hitXform = Transform(hitEnt);
                    if (hitXform.Coordinates.EntityId != hitXform.GridUid && hitXform.GridUid != null)
                        hitPos = _transformSystem.WithEntityId(hitPos, hitXform.GridUid.Value);

                    // Set velocity for the frame of impact
                    if (comp.RaycastResetVelocity == null)
                    {
                        var parentVel = _physics.GetMapLinearVelocity(xform.ParentUid);
                        comp.RaycastResetVelocity = velocity + parentVel;
                        var curVel = body.LinearVelocity;
                        curVel.Normalize();
                        curVel *= 1f / frameTime;
                        _physics.SetLinearVelocity(uid, curVel, body: body);
                    }

                    _transformSystem.SetCoordinates(uid, hitPos);
                    return true;
                }
                return false;
            }
        }
        // Exodus-OptimizeProjectiles-End
    }
}

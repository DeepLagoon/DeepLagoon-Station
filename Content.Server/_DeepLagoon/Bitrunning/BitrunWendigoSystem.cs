using System.Numerics;
using Content.Server.NPC.HTN;
using Content.Shared.ActionBlocker;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Maps;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.Physics;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

public enum BitrunWendigoAttack : byte { Slam, Teleport, Alternating, Spiral, Wave }

[RegisterComponent]
public sealed partial class BitrunWendigoComponent : Component
{
    public EntityCoordinates Home;
    public bool Enraged;
    public bool Rooted;
    public TimeSpan NextAttack;
    public TimeSpan NextScream;
    public TimeSpan ReleaseAt;
    public TimeSpan NextStomp;
    public EntityCoordinates LastPosition;
    public EntityCoordinates SlamOrigin;
    public int SlamRing = -1;
    public TimeSpan NextRing;
    public EntityUid? Target;
    public readonly List<WendigoShot> Shots = new();
    public int FiredShots;
}

public readonly record struct WendigoShot(TimeSpan At, double Angle, float Speed, bool Spiral = false, float Curve = 0);

[RegisterComponent]
public sealed partial class BitrunCurvingProjectileComponent : Component
{
    public float DegreesPerMeter;
}

/// <summary>WhiteMoon wendigo.dm, ground_slam.dm, teleport.dm and projectileattack.dm.</summary>
public sealed class BitrunWendigoSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private MobStateSystem _mobs = default!;
    [Dependency] private SharedGunSystem _guns = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private TurfSystem _turf = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private ThrowingSystem _throwing = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private MovementSpeedModifierSystem _movement = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunWendigoComponent, MapInitEvent>((Entity<BitrunWendigoComponent> ent, ref MapInitEvent args) =>
        {
            ent.Comp.Home = ent.Comp.LastPosition = Transform(ent).Coordinates;
        });
        SubscribeLocalEvent<BitrunWendigoComponent, DamageChangedEvent>((Entity<BitrunWendigoComponent> ent, ref DamageChangedEvent args) =>
        {
            var rage = args.Damageable.TotalDamage >= 625;
            if (rage == ent.Comp.Enraged) return;
            ent.Comp.Enraged = rage;
            _movement.RefreshMovementSpeedModifiers(ent);
        });
        SubscribeLocalEvent<BitrunWendigoComponent, RefreshMovementSpeedModifiersEvent>((Entity<BitrunWendigoComponent> ent, ref RefreshMovementSpeedModifiersEvent args) =>
        {
            // Source move_to_delay drops from six to four in rage.
            if (ent.Comp.Enraged) args.ModifySpeed(1.5f, 1.5f);
        });
        SubscribeLocalEvent<BitrunWendigoComponent, UpdateCanMoveEvent>((Entity<BitrunWendigoComponent> ent, ref UpdateCanMoveEvent args) =>
        {
            if (ent.Comp.Rooted) args.Cancel();
        });
    }

    private void Root(Entity<BitrunWendigoComponent> ent, bool rooted)
    {
        if (ent.Comp.Rooted == rooted) return;
        ent.Comp.Rooted = rooted;
        _blocker.UpdateCanMove(ent);
    }

    public bool StartAttack(Entity<BitrunWendigoComponent> boss, EntityUid target, BitrunWendigoAttack attack)
    {
        if (_mobs.IsDead(boss) || Deleted(target) || _mobs.IsDead(target) || boss.Comp.Rooted ||
            boss.Comp.NextAttack > _timing.CurTime || Transform(boss).MapUid != Transform(target).MapUid ||
            attack == BitrunWendigoAttack.Wave && !boss.Comp.Enraged)
            return false;
        boss.Comp.Target = target;
        boss.Comp.NextAttack = _timing.CurTime + TimeSpan.FromSeconds(10);
        if (attack == BitrunWendigoAttack.Teleport)
        {
            var origin = Transform(target).Coordinates;
            var ends = new List<EntityCoordinates>();
            for (var x = -6; x <= 6; x++)
            for (var y = -6; y <= 6; y++)
            {
                if (Math.Max(Math.Abs(x), Math.Abs(y)) != 6) continue;
                var end = origin.Offset(new Vector2(x, y));
                if (_turf.TryGetTileRef(end, out var tile) && !tile.Value.Tile.IsEmpty && !_turf.IsTileBlocked(tile.Value, CollisionGroup.Impassable))
                    ends.Add(end);
            }
            if (ends.Count > 0) _transform.SetCoordinates(boss, _random.Pick(ends));
            if (boss.Comp.Enraged)
            {
                var delta = _transform.GetWorldPosition(target) - _transform.GetWorldPosition(boss);
                var aim = Math.Atan2(delta.Y, delta.X) * 180 / Math.PI;
                foreach (var spread in new[] {-20, -10, 0, 10, 20})
                    boss.Comp.Shots.Add(new(_timing.CurTime, aim + spread, 1.25f));
            }
            return true;
        }
        Root(boss, true);
        if (attack == BitrunWendigoAttack.Slam)
        {
            boss.Comp.SlamOrigin = Transform(boss).Coordinates;
            boss.Comp.SlamRing = 0;
            boss.Comp.NextRing = _timing.CurTime;
            boss.Comp.ReleaseAt = _timing.CurTime + TimeSpan.FromSeconds(1.8);
            return true;
        }
        _transform.SetCoordinates(boss, boss.Comp.Home);
        boss.Comp.NextScream = _timing.CurTime + TimeSpan.FromSeconds(10);
        // wendigo_scream waits 0.5 + 1 seconds before the projectile sequence.
        var start = _timing.CurTime + TimeSpan.FromSeconds(1.5);
        void Add(float delay, double angle, float speed, bool spiral = false, float curve = 16) =>
            boss.Comp.Shots.Add(new(start + TimeSpan.FromSeconds(delay), angle, speed, spiral, curve));
        var direction = _random.Prob(0.5f) ? 1 : -1;
        if (attack == BitrunWendigoAttack.Alternating)
        {
            for (var wave = 1; wave <= 8; wave++)
            for (var shot = 1; shot <= 24; shot++)
                Add((wave - 1) * (boss.Comp.Enraged ? 0.4f : 0.6f), shot * 15 + wave % 2 * 7.5, boss.Comp.Enraged ? 5 : 3.3f);
            boss.Comp.ReleaseAt = start + TimeSpan.FromSeconds(8 * (boss.Comp.Enraged ? 0.4 : 0.6) + 3);
        }
        else if (attack == BitrunWendigoAttack.Spiral)
        {
            var delta = _transform.GetWorldPosition(target) - _transform.GetWorldPosition(boss);
            var aim = Math.Atan2(delta.Y, delta.X) * 180 / Math.PI;
            var perStep = boss.Comp.Enraged ? 2 : 5;
            // The inherited enraged spiral starts BOTH source sequences after one second.
            for (var spiral = 0; spiral < (boss.Comp.Enraged ? 2 : 1); spiral++)
            {
                var sign = _random.Prob(0.5f) ? 1 : -1;
                for (var step = 1; step <= 40; step++)
                for (var shot = 1; shot <= perStep; shot++)
                    Add((boss.Comp.Enraged ? 1 : 0) + (step - 1) * 0.1f,
                        aim + step * (5 + (boss.Comp.Enraged ? step / 6.0 : 0)) * sign + shot * 360.0 / perStep, 5, true);
            }
            boss.Comp.ReleaseAt = start + TimeSpan.FromSeconds((boss.Comp.Enraged ? 1 : 0) + 7);
        }
        else
        {
            for (var wave = 1; wave <= 12; wave++)
            for (var shot = 1; shot <= 6; shot++)
                Add((wave - 1) * 0.6f, shot * 60 - wave * 5 * direction, 1.25f, curve: 9.6f * direction);
            boss.Comp.ReleaseAt = start + TimeSpan.FromSeconds(10.2);
        }
        boss.Comp.Shots.Sort((a, b) => a.At.CompareTo(b.At));
        boss.Comp.NextAttack = boss.Comp.ReleaseAt;
        return true;
    }

    private void Slam(EntityUid boss, EntityCoordinates origin, int ring, float throwRange)
    {
        var position = _transform.ToMapCoordinates(origin);
        foreach (var target in _lookup.GetEntitiesInRange<MobStateComponent>(origin, (ring + 0.75f) * 1.42f))
        {
            if (target.Owner == boss || HasComp<ThrownItemComponent>(target)) continue;
            var delta = _transform.GetWorldPosition(target) - position.Position;
            var distance = MathF.Max(MathF.Abs(delta.X), MathF.Abs(delta.Y));
            if (distance < MathF.Max(0, ring - 0.5f) || distance >= ring + 0.5f) continue;
            var damage = new DamageSpecifier();
            damage.DamageDict["Blunt"] = 20;
            _damage.TryChangeDamage(target, damage, origin: boss);
            if (delta.LengthSquared() > 0.01f)
                _throwing.TryThrow(target, Vector2.Normalize(delta) * throwRange, 2, user: boss, recoil: false, doSpin: false);
        }
    }

    public override void Update(float frameTime)
    {
        var curves = EntityQueryEnumerator<BitrunCurvingProjectileComponent, PhysicsComponent>();
        while (curves.MoveNext(out var uid, out var curve, out var body))
        {
            var velocity = body.LinearVelocity;
            var angle = curve.DegreesPerMeter * velocity.Length() * frameTime * MathF.PI / 180;
            _physics.SetLinearVelocity(uid, new Vector2(velocity.X * MathF.Cos(angle) - velocity.Y * MathF.Sin(angle),
                velocity.X * MathF.Sin(angle) + velocity.Y * MathF.Cos(angle)), body: body);
        }
        var query = EntityQueryEnumerator<BitrunWendigoComponent, HTNComponent>();
        while (query.MoveNext(out var uid, out var comp, out var htn))
        {
            if (_mobs.IsDead(uid)) { comp.Shots.Clear(); comp.SlamRing = -1; Root((uid, comp), false); continue; }
            if (comp.Rooted && comp.ReleaseAt <= _timing.CurTime) Root((uid, comp), false);
            if (comp.SlamRing >= 0 && comp.NextRing <= _timing.CurTime)
            {
                Slam(uid, comp.SlamOrigin, comp.SlamRing, 8);
                comp.NextRing += TimeSpan.FromSeconds(0.3);
                if (++comp.SlamRing > 5) comp.SlamRing = -1;
            }
            var current = Transform(uid).Coordinates;
            if (current.EntityId == comp.LastPosition.EntityId && (current.Position - comp.LastPosition.Position).LengthSquared() >= 1 && comp.NextStomp <= _timing.CurTime)
            {
                Slam(uid, current, 0, 8);
                Slam(uid, current, 1, 8);
                comp.LastPosition = current;
                comp.NextStomp = _timing.CurTime + TimeSpan.FromSeconds(0.1);
            }
            while (comp.Shots.Count > 0 && comp.Shots[0].At <= _timing.CurTime)
            {
                var shot = comp.Shots[0];
                comp.Shots.RemoveAt(0);
                var projectile = Spawn(shot.Spiral ? "BitrunWendigoSpiral" : "BitrunWendigoBolt", current);
                if (shot.Curve != 0) EnsureComp<BitrunCurvingProjectileComponent>(projectile).DegreesPerMeter = shot.Curve;
                var angle = shot.Angle * Math.PI / 180;
                _guns.ShootProjectile(projectile, new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)), Vector2.Zero, uid, uid, shot.Speed);
                comp.FiredShots++;
            }
            if (!htn.Enabled || comp.NextAttack > _timing.CurTime || comp.Rooted ||
                !htn.Blackboard.TryGetValue<EntityUid>("Target", out var target, EntityManager)) continue;
            var choice = _random.Next(1, comp.NextScream <= _timing.CurTime ? 4 : 3);
            var attack = choice == 1 ? BitrunWendigoAttack.Slam : choice == 2 ? BitrunWendigoAttack.Teleport :
                _random.Pick(comp.Enraged ? new[] {BitrunWendigoAttack.Alternating, BitrunWendigoAttack.Spiral, BitrunWendigoAttack.Wave} :
                    new[] {BitrunWendigoAttack.Alternating, BitrunWendigoAttack.Spiral});
            StartAttack((uid, comp), target, attack);
        }
    }
}

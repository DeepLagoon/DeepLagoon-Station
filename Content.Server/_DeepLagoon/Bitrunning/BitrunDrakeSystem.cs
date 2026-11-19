using System.Linq;
using System.Numerics;
using Content.Server.NPC.HTN;
using Content.Shared.ActionBlocker;
using Content.Shared.Damage;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.Physics;
using Content.Shared.Throwing;
using Content.Shared._DeepLagoon.Bitrunning;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

public enum BitrunDrakeAttack : byte { Cone, Meteors, Swoop, MassFire }
public enum BitrunDrakeStage : byte { Ground, Rise, Flight, Chase, Arena, Descent, Recover }

[RegisterComponent]
public sealed partial class BitrunDrakeComponent : Component
{
    public BitrunDrakeStage Stage;
    public bool Enraged;
    public bool Invulnerable;
    public bool SavedCollision;
    public bool FollowUp;
    public bool ArenaFailed;
    public int ArenaRound;
    public EntityUid? Target;
    public EntityCoordinates ArenaCenter;
    public TimeSpan NextStage;
    public TimeSpan NextMove;
    public TimeSpan NextAttack;
    public TimeSpan EscapeRageUntil;
    public readonly List<DrakeEvent> Events = new();
    public readonly HashSet<EntityUid> ArenaPlayers = new();
    public readonly List<EntityUid> ArenaWalls = new();
}

public sealed record DrakeEvent(TimeSpan At, EntityCoordinates Position, string Kind, HashSet<EntityUid>? Hits = null);

/// <summary>drake.dm, lava_swoop.dm, fire_breath.dm and meteors.dm from the pinned source.</summary>
public sealed class BitrunDrakeSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private MobStateSystem _mobs = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private BitrunBossHazardSystem _hazards = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private ThrowingSystem _throwing = default!;
    [Dependency] private MovementSpeedModifierSystem _speed = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunDrakeComponent, BeforeDamageChangedEvent>((Entity<BitrunDrakeComponent> ent, ref BeforeDamageChangedEvent args) =>
        {
            if (ent.Comp.Invulnerable) args.Cancelled = true;
        });
        SubscribeLocalEvent<BitrunDrakeComponent, DamageChangedEvent>((Entity<BitrunDrakeComponent> ent, ref DamageChangedEvent args) => ent.Comp.Enraged = args.Damageable.TotalDamage > 625);
        SubscribeLocalEvent<BitrunDrakeComponent, RefreshMovementSpeedModifiersEvent>((Entity<BitrunDrakeComponent> ent, ref RefreshMovementSpeedModifiersEvent args) =>
        {
            if (ent.Comp.EscapeRageUntil > _timing.CurTime) args.ModifySpeed(2, 2);
        });
        SubscribeLocalEvent<BitrunDrakeComponent, UpdateCanMoveEvent>((Entity<BitrunDrakeComponent> ent, ref UpdateCanMoveEvent args) =>
        {
            if (ent.Comp.Stage != BitrunDrakeStage.Ground) args.Cancel();
        });
        SubscribeLocalEvent<BitrunDrakeComponent, Content.Shared.Weapons.Melee.Events.AttemptMeleeEvent>((Entity<BitrunDrakeComponent> ent, ref Content.Shared.Weapons.Melee.Events.AttemptMeleeEvent args) =>
        {
            if (ent.Comp.Stage != BitrunDrakeStage.Ground) args.Cancelled = true;
        });
    }

    public bool StartAttack(Entity<BitrunDrakeComponent> boss, EntityUid target, BitrunDrakeAttack attack, bool followUp = false)
    {
        if (_mobs.IsDead(boss) || Deleted(target) || _mobs.IsDead(target) ||
            boss.Comp.Stage != BitrunDrakeStage.Ground || boss.Comp.NextAttack > _timing.CurTime ||
            Transform(boss).MapUid != Transform(target).MapUid) return false;
        boss.Comp.Target = target;
        boss.Comp.NextAttack = _timing.CurTime + TimeSpan.FromSeconds(attack == BitrunDrakeAttack.MassFire ? 10.5 : 3);
        switch (attack)
        {
            case BitrunDrakeAttack.Cone: Cone(boss, target); break;
            case BitrunDrakeAttack.Meteors: Meteors(boss, target); break;
            case BitrunDrakeAttack.MassFire: MassFire(boss); break;
            case BitrunDrakeAttack.Swoop:
                boss.Comp.FollowUp = followUp;
                boss.Comp.ArenaFailed = false;
                boss.Comp.ArenaPlayers.Clear();
                boss.Comp.ArenaWalls.Clear();
                boss.Comp.ArenaRound = 0;
                boss.Comp.Stage = BitrunDrakeStage.Rise;
                boss.Comp.NextStage = _timing.CurTime + TimeSpan.FromSeconds(0.3);
                if (TryComp<PhysicsComponent>(boss, out var body))
                {
                    boss.Comp.SavedCollision = body.CanCollide;
                    _physics.SetCanCollide(boss, false, body: body);
                }
                _appearance.SetData(boss, BitrunEquipmentVisuals.Mode, "shadow");
                _blocker.UpdateCanMove(boss);
                if (!boss.Comp.Enraged)
                    for (var i = 0; i < 30; i++) boss.Comp.Events.Add(new(_timing.CurTime + TimeSpan.FromSeconds(i * 0.08), default, "pool"));
                break;
        }
        boss.Comp.Events.Sort((a, b) => a.At.CompareTo(b.At));
        return true;
    }

    private void Line(Entity<BitrunDrakeComponent> boss, double angle, float delay)
    {
        var origin = _hazards.Center(Transform(boss).Coordinates);
        var hits = new HashSet<EntityUid>();
        var step = 0;
        foreach (var cell in BitrunBossGeometry.Line(origin, angle, 15))
        {
            if (_hazards.Blocked(cell)) break;
            boss.Comp.Events.Add(new(_timing.CurTime + TimeSpan.FromSeconds(delay + step++ * 0.15), cell, "fire", hits));
        }
    }

    private void Cone(Entity<BitrunDrakeComponent> boss, EntityUid target)
    {
        var delta = _transform.GetWorldPosition(target) - _transform.GetWorldPosition(boss);
        var angle = Math.Atan2(delta.Y, delta.X);
        foreach (var offset in new[] { -40, 0, 40 }) Line(boss, angle + offset * Math.PI / 180, 0);
    }

    private void MassFire(Entity<BitrunDrakeComponent> boss)
    {
        for (var spin = 1; spin <= 3; spin++)
        for (var sector = 1; sector <= 12; sector++)
            Line(boss, (30 * sector + spin * 15) * Math.PI / 180, (spin - 1) * 2.5f);
    }

    private void Meteors(Entity<BitrunDrakeComponent> boss, EntityUid target)
    {
        var center = _hazards.Center(Transform(target).Coordinates);
        for (var x = -9; x <= 9; x++)
        for (var y = -9; y <= 9; y++)
            if (_random.Prob(0.11f)) _hazards.Telegraph(center.Offset(new Vector2(x, y)), boss, "BitrunDrakeMeteor", 0.9f,
                10, ignoreArmor: true, mine: true, hotspot: true);
        // Source meteors pass no shared flame_hit list, so target/fall's actual
        // else branch deals 10, rather than the conditional 40-damage branch.
    }

    private void Descent(Entity<BitrunDrakeComponent> boss)
    {
        boss.Comp.Stage = BitrunDrakeStage.Descent;
        boss.Comp.NextStage = _timing.CurTime + TimeSpan.FromSeconds(1);
        Spawn("BitrunDrakeLanding", Transform(boss).Coordinates);
    }

    private void ArenaRound(Entity<BitrunDrakeComponent> boss)
    {
        var center = boss.Comp.ArenaCenter;
        var players = _lookup.GetEntitiesInRange<MobStateComponent>(center, 3.6f)
            .Where(ent => HasComp<ActorComponent>(ent) && BitrunBossGeometry.Chebyshev(_transform.ToMapCoordinates(Transform(ent).Coordinates), _transform.ToMapCoordinates(center)) <= 2.1f).ToArray();
        if (players.Length == 0)
        {
            boss.Comp.ArenaFailed = boss.Comp.ArenaPlayers.Any(uid => !Deleted(uid) && HasComp<ActorComponent>(uid));
            foreach (var wall in boss.Comp.ArenaWalls) if (!Deleted(wall)) QueueDel(wall);
            Descent(boss);
            return;
        }
        var safe = new HashSet<Vector2>();
        foreach (var player in players)
        {
            boss.Comp.ArenaPlayers.Add(player);
            var candidates = new List<Vector2>();
            for (var x = -2; x <= 2; x++)
            for (var y = -2; y <= 2; y++)
            {
                var offset = new Vector2(x, y);
                var cell = center.Offset(offset);
                var distance = BitrunBossGeometry.Chebyshev(_transform.ToMapCoordinates(cell), _transform.GetMapCoordinates(player));
                if (distance >= 1.9f && distance <= 2.1f && !safe.Contains(offset) && !_hazards.Blocked(cell)) candidates.Add(offset);
            }
            if (candidates.Count > 0) safe.Add(_random.Pick(candidates));
        }
        for (var x = -2; x <= 2; x++)
        for (var y = -2; y <= 2; y++)
        {
            var offset = new Vector2(x, y);
            var cell = center.Offset(offset);
            if (safe.Contains(offset)) Spawn("BitrunDrakeSafeGround", cell);
            else _hazards.Telegraph(cell, boss, "BitrunDrakeLavaWarning", 1.3f, 10, ignoreArmor: true, lavaDuration: 1);
        }
        boss.Comp.ArenaRound++;
        boss.Comp.NextStage = _timing.CurTime + TimeSpan.FromSeconds(2.4);
    }

    private void FinishSwoop(Entity<BitrunDrakeComponent> boss)
    {
        boss.Comp.Invulnerable = false;
        if (TryComp<PhysicsComponent>(boss, out var body)) _physics.SetCanCollide(boss, boss.Comp.SavedCollision, body: body);
        boss.Comp.Stage = BitrunDrakeStage.Ground;
        _blocker.UpdateCanMove(boss);
        _appearance.SetData(boss, BitrunEquipmentVisuals.Mode, _mobs.IsDead(boss) ? "dead" : "ground");
        boss.Comp.NextAttack = _timing.CurTime + TimeSpan.FromSeconds(4);
        if (_mobs.IsDead(boss)) return;
        if (boss.Comp.ArenaFailed)
        {
            BitrunBossGeometry.Heal(_damage, boss, Comp<DamageableComponent>(boss), 250);
            boss.Comp.EscapeRageUntil = _timing.CurTime + TimeSpan.FromSeconds(5);
            _speed.RefreshMovementSpeedModifiers(boss);
            _appearance.SetData(boss, BitrunEquipmentVisuals.Mode, "escape");
            boss.Comp.Events.Add(new(_timing.CurTime + TimeSpan.FromSeconds(5), default, "mass"));
        }
        else if (boss.Comp.FollowUp && boss.Comp.Target is {} target && !Deleted(target) && !boss.Comp.Enraged)
        {
            Cone(boss, target);
            Meteors(boss, target);
        }
        boss.Comp.Events.Sort((a, b) => a.At.CompareTo(b.At));
    }

    public override void Update(float frameTime)
    {
        var bosses = EntityQueryEnumerator<BitrunDrakeComponent, HTNComponent>();
        while (bosses.MoveNext(out var uid, out var comp, out var htn))
        {
            var boss = new Entity<BitrunDrakeComponent>(uid, comp);
            if (_mobs.IsDead(uid))
            {
                comp.Events.Clear();
                if (comp.Stage != BitrunDrakeStage.Ground) FinishSwoop(boss);
                continue;
            }
            while (comp.Events.Count > 0 && comp.Events[0].At <= _timing.CurTime)
            {
                var ev = comp.Events[0]; comp.Events.RemoveAt(0);
                if (ev.Kind == "fire") _hazards.Telegraph(ev.Position, uid, "BitrunDrakeFlame", 0, 20, ignoreArmor: true, hotspot: true, hits: ev.Hits);
                else if (ev.Kind == "mass")
                {
                    comp.EscapeRageUntil = default;
                    _speed.RefreshMovementSpeedModifiers(uid);
                    _appearance.SetData(uid, BitrunEquipmentVisuals.Mode, "ground");
                    MassFire(boss);
                    comp.Events.Sort((a, b) => a.At.CompareTo(b.At));
                }
                else if (comp.Target is {} target && !Deleted(target))
                    _hazards.Telegraph(_hazards.Center(Transform(target).Coordinates).Offset(new Vector2(_random.Next(-1, 2), _random.Next(-1, 2))),
                        uid, "BitrunDrakeLavaWarning", 1.3f, 10, ignoreArmor: true, lavaDuration: 6);
            }
            if (comp.Stage != BitrunDrakeStage.Ground && comp.Target is {} victim && !Deleted(victim))
            {
                if (comp.Stage == BitrunDrakeStage.Rise && comp.NextStage <= _timing.CurTime)
                {
                    comp.Invulnerable = true; comp.Stage = BitrunDrakeStage.Flight;
                    comp.NextStage = _timing.CurTime + TimeSpan.FromSeconds(0.7);
                }
                else if (comp.Stage == BitrunDrakeStage.Flight && comp.NextStage <= _timing.CurTime) comp.Stage = BitrunDrakeStage.Chase;
                if (comp.Stage == BitrunDrakeStage.Chase && comp.NextMove <= _timing.CurTime)
                {
                    var current = _hazards.Center(Transform(uid).Coordinates);
                    var destination = _hazards.Center(Transform(victim).Coordinates);
                    if (current.EntityId != destination.EntityId) { Descent(boss); continue; }
                    var delta = destination.Position - current.Position;
                    if (delta.LengthSquared() > 0.25f)
                    {
                        _transform.SetCoordinates(uid, current.Offset(new Vector2(Math.Sign(delta.X), Math.Sign(delta.Y))));
                        comp.NextMove = _timing.CurTime + TimeSpan.FromSeconds(0.05);
                    }
                    else if (comp.Enraged)
                    {
                        comp.ArenaCenter = current; comp.Stage = BitrunDrakeStage.Arena;
                        comp.NextStage = _timing.CurTime + TimeSpan.FromSeconds(1);
                        foreach (var cell in BitrunBossGeometry.Ring(current, 3)) comp.ArenaWalls.Add(_hazards.Barrier(cell, uid, 8.2f, false));
                        foreach (var cell in BitrunBossGeometry.Square(current, 2)) _hazards.Mine(cell, uid);
                    }
                    else Descent(boss);
                }
                if (comp.Stage == BitrunDrakeStage.Arena && comp.NextStage <= _timing.CurTime)
                {
                    if (comp.ArenaRound < 3) ArenaRound(boss); else Descent(boss);
                }
                if (comp.Stage == BitrunDrakeStage.Descent && comp.NextStage <= _timing.CurTime)
                {
                    var origin = _transform.GetMapCoordinates(uid);
                    foreach (var target in _lookup.GetEntitiesInRange<MobStateComponent>(Transform(uid).Coordinates, 2.2f))
                    {
                        var delta = _transform.GetWorldPosition(target) - origin.Position;
                        if (target.Owner == uid || MathF.Max(Math.Abs(delta.X), Math.Abs(delta.Y)) > 1.49f) continue;
                        _hazards.DirectDamage(target, 75, "Blunt", uid, ignoreArmor: true);
                        if (delta.LengthSquared() < 0.01f) delta = Vector2.UnitX;
                        _throwing.TryThrow(target, Vector2.Normalize(delta) * 3, 2, user: uid, recoil: false, doSpin: false);
                    }
                    comp.Stage = BitrunDrakeStage.Recover; comp.NextStage = _timing.CurTime + TimeSpan.FromSeconds(0.1);
                }
                if (comp.Stage == BitrunDrakeStage.Recover && comp.NextStage <= _timing.CurTime) FinishSwoop(boss);
            }
            else if (comp.Stage != BitrunDrakeStage.Ground) FinishSwoop(boss);
            if (!htn.Enabled || comp.Stage != BitrunDrakeStage.Ground || comp.NextAttack > _timing.CurTime ||
                !htn.Blackboard.TryGetValue<EntityUid>("Target", out var currentTarget, EntityManager)) continue;
            var anger = Math.Clamp((float)(CompOrNull<DamageableComponent>(uid)?.TotalDamage ?? 0) / 60, 0, 20);
            var attack = _random.Prob((15 + anger) / 100) ? BitrunDrakeAttack.Swoop :
                comp.Enraged && _random.Prob((10 + anger) / 100) ? BitrunDrakeAttack.MassFire : BitrunDrakeAttack.Cone;
            StartAttack(boss, currentTarget, attack, true);
            if (attack == BitrunDrakeAttack.Cone && _random.Prob(0.5f)) Meteors(boss, currentTarget);
        }
    }
}

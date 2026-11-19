using System.Linq;
using System.Numerics;
using Content.Server.Fluids.EntitySystems;
using Content.Server.NPC.HTN;
using Content.Shared.ActionBlocker;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.Fluids.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared._DeepLagoon.Bitrunning;
using Robust.Shared.Map;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

public enum BitrunBubblegumAttack : byte { TripleCharge, HallucinationCharge, Surround }

[RegisterComponent]
public sealed partial class BitrunBubblegumComponent : Component
{
    public bool Smash;
    public float Anger;
    public float EnrageDuration = 7;
    public TimeSpan EnrageUntil;
    public TimeSpan NextAttack;
    public TimeSpan NextSequence;
    public EntityUid? Target;
    public BitrunBubblegumAttack Attack;
    public int Sequence;
    public int Remaining;
    public bool EnragedPattern;
    public bool WasEnraged;
    public EntityCoordinates? LastBloodTile;
    public readonly List<BubblegumEvent> Events = new();
}

public sealed record BubblegumEvent(TimeSpan At, string Kind, EntityCoordinates Position, EntityUid? Target = null);

[RegisterComponent]
public sealed partial class BitrunBubblegumChargeComponent : Component
{
    public EntityUid Caster;
    public EntityCoordinates Destination;
    public TimeSpan NextStep;
    public bool Clone;
    public readonly HashSet<EntityUid> Hit = new();
    public int Steps;
}

/// <summary>Blood warp, two independently delayed hands and the source charge sequences.</summary>
public sealed class BitrunBubblegumSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private PuddleSystem _puddles = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private MobStateSystem _mobs = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private MovementSpeedModifierSystem _speed = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private BitrunBossHazardSystem _hazards = default!;
    [Dependency] private BitrunMegafaunaSystem _megafauna = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunBubblegumComponent, DamageChangedEvent>((Entity<BitrunBubblegumComponent> ent, ref DamageChangedEvent args) =>
        {
            ent.Comp.Smash = args.Damageable.TotalDamage >= 625;
            ent.Comp.Anger = Math.Clamp(args.Damageable.TotalDamage.Float() / 60, 0, 20);
            ent.Comp.EnrageDuration = 7 * Math.Clamp(ent.Comp.Anger / 20, 0.5f, 1);
        });
        SubscribeLocalEvent<BitrunBubblegumComponent, ProjectileReflectAttemptEvent>((Entity<BitrunBubblegumComponent> ent, ref ProjectileReflectAttemptEvent args) =>
        {
            if (ent.Comp.EnrageUntil <= _timing.CurTime) return;
            args.Cancelled = true;
            QueueDel(args.ProjUid);
        });
        SubscribeLocalEvent<BitrunBubblegumComponent, RefreshMovementSpeedModifiersEvent>((Entity<BitrunBubblegumComponent> ent, ref RefreshMovementSpeedModifiersEvent args) =>
        {
            if (ent.Comp.EnrageUntil > _timing.CurTime) args.ModifySpeed(4f / 3, 4f / 3);
        });
        SubscribeLocalEvent<BitrunBubblegumComponent, UpdateCanMoveEvent>((Entity<BitrunBubblegumComponent> ent, ref UpdateCanMoveEvent args) =>
        {
            if (ent.Comp.Remaining > 0 || HasComp<BitrunBubblegumChargeComponent>(ent)) args.Cancel();
        });
        SubscribeLocalEvent<BitrunBubblegumComponent, AttemptMeleeEvent>((Entity<BitrunBubblegumComponent> ent, ref AttemptMeleeEvent args) =>
        {
            if (ent.Comp.Remaining > 0 || HasComp<BitrunBubblegumChargeComponent>(ent)) args.Cancelled = true;
        });
    }

    public void LeaveBlood(EntityCoordinates position)
    {
        // Real native blood: cleaning and reagent removal also remove warp eligibility.
        if (!Pools(position, 0).Any()) _puddles.TrySpillAt(position, new Solution("Blood", 5), out _, sound: false);
    }

    private IEnumerable<EntityCoordinates> Pools(EntityCoordinates center, int radius) =>
        _lookup.GetEntitiesInRange<PuddleComponent>(center, radius * 1.42f + 0.75f)
            .Where(pool => _solutions.TryGetSolution(pool.Owner, pool.Comp.SolutionName, out _, out var solution) &&
                solution.GetTotalPrototypeQuantity("Blood") > 0 &&
                BitrunBossGeometry.Chebyshev(_transform.ToMapCoordinates(center), _transform.GetMapCoordinates(pool)) < radius + 0.75f)
            .Select(pool => _hazards.Center(Transform(pool).Coordinates));

    private EntityCoordinates[] WarpDestinations(EntityUid target) => Pools(Transform(target).Coordinates, 5)
        .Where(pool => BitrunBossGeometry.Chebyshev(_transform.ToMapCoordinates(pool), _transform.GetMapCoordinates(target)) >= 4.75f).ToArray();

    public bool BloodWarp(Entity<BitrunBubblegumComponent> boss, EntityUid target)
    {
        if (Deleted(target) || Transform(boss).MapUid != Transform(target).MapUid ||
            BitrunBossGeometry.Chebyshev(_transform.GetMapCoordinates(boss), _transform.GetMapCoordinates(target)) <= 1 ||
            !Pools(Transform(boss).Coordinates, 1).Any() || WarpDestinations(target).Length == 0) return false;
        boss.Comp.Events.Add(new(_timing.CurTime + TimeSpan.FromSeconds(0.3), "warp", default, target));
        boss.Comp.Events.Sort((a, b) => a.At.CompareTo(b.At));
        return true;
    }

    public void BloodHand(Entity<BitrunBubblegumComponent> boss, EntityCoordinates position, bool grab, bool right)
    {
        Spawn(grab ? right ? "BitrunBloodRightGrab" : "BitrunBloodLeftGrab" : right ? "BitrunBloodRightSmack" : "BitrunBloodLeftSmack", position);
        boss.Comp.Events.Add(new(_timing.CurTime + TimeSpan.FromSeconds(grab ? 0.6 : 0.4), grab ? "grab" : "smack", position));
        boss.Comp.Events.Sort((a, b) => a.At.CompareTo(b.At));
    }

    private bool BloodAttack(Entity<BitrunBubblegumComponent> boss)
    {
        var targets = _lookup.GetEntitiesInRange<MobStateComponent>(Transform(boss).Coordinates, 9)
            .Where(ent => ent.Owner != boss.Owner && !HasComp<BitrunMegafaunaComponent>(ent) && Pools(Transform(ent).Coordinates, 0).Any())
            .OrderBy(_ => _random.Next()).Take(2).ToArray();
        if (targets.Length == 0) return false;
        for (var i = 0; i < 2; i++)
        {
            var target = targets[i % targets.Length];
            BloodHand(boss, _hazards.Center(Transform(target).Coordinates), !_mobs.IsAlive(target) || _random.Prob(0.1f), i == 0);
        }
        return true;
    }

    public bool StartAttack(Entity<BitrunBubblegumComponent> boss, EntityUid target, BitrunBubblegumAttack attack)
    {
        if (_mobs.IsDead(boss) || Deleted(target) || _mobs.IsDead(target) || boss.Comp.NextAttack > _timing.CurTime ||
            boss.Comp.Remaining > 0 || HasComp<BitrunBubblegumChargeComponent>(boss) || Transform(boss).MapUid != Transform(target).MapUid) return false;
        boss.Comp.Target = target; boss.Comp.Attack = attack; boss.Comp.Sequence = 0;
        boss.Comp.EnragedPattern = boss.Comp.Smash && !_random.Prob(0.33f);
        boss.Comp.Remaining = attack == BitrunBubblegumAttack.TripleCharge ? 3 : attack == BitrunBubblegumAttack.Surround ? 5 : boss.Comp.EnragedPattern ? 6 : 1;
        boss.Comp.NextSequence = _timing.CurTime;
        _blocker.UpdateCanMove(boss);
        return true;
    }

    private void Charge(EntityUid actor, EntityUid caster, EntityCoordinates target, float warning, int past, bool clone)
    {
        var origin = _hazards.Center(Transform(actor).Coordinates);
        var delta = target.Position - origin.Position;
        if (delta.LengthSquared() > 0.01f) target = target.Offset(new Vector2(Math.Sign(delta.X), Math.Sign(delta.Y)) * past);
        var charge = EnsureComp<BitrunBubblegumChargeComponent>(actor);
        charge.Caster = caster; charge.Destination = target; charge.Clone = clone; charge.Steps = 0; charge.Hit.Clear();
        charge.NextStep = _timing.CurTime + TimeSpan.FromSeconds(warning);
        Spawn("BitrunBubblegumLanding", target);
    }

    private void RingCharge(Entity<BitrunBubblegumComponent> boss, EntityCoordinates target, int count, int radius, float warning, int past, bool self)
    {
        var angle = _random.NextDouble() * Math.Tau;
        for (var i = 0; i < count; i++)
        {
            var a = angle + i * Math.Tau / count;
            var origin = _hazards.Center(target.Offset(new Vector2(MathF.Round((float)Math.Cos(a) * radius), MathF.Round((float)Math.Sin(a) * radius))));
            var actor = self && i == 0 ? boss.Owner : Spawn("BitrunBubblegumHallucination", origin);
            _transform.SetCoordinates(actor, origin);
            Charge(actor, boss, target, warning, past, actor != boss.Owner);
        }
    }

    private void Sequence(Entity<BitrunBubblegumComponent> boss, EntityUid target)
    {
        var at = _hazards.Center(Transform(target).Coordinates);
        var index = boss.Comp.Sequence++;
        if (boss.Comp.Attack == BitrunBubblegumAttack.TripleCharge)
            Charge(boss, boss, at, 0.6f - index * 0.2f, 2, false);
        else if (boss.Comp.Attack == BitrunBubblegumAttack.Surround)
        {
            RingCharge(boss, at, 2, 2, 0.8f, 2, false);
            Charge(boss, boss, at, 0.6f, 2, false);
        }
        else if (!boss.Comp.EnragedPattern) RingCharge(boss, at, 6, 6, 0.8f, 0, true);
        else if (index < 3) RingCharge(boss, at, 4, 4, 0.9f - index * 0.2f, 0, true);
        else Charge(boss, boss, at, 0.6f - (index - 3) * 0.2f, 2, false);
        boss.Comp.Remaining--;
    }

    private void FinishCharge(EntityUid actor, BitrunBubblegumChargeComponent charge)
    {
        RemComp<BitrunBubblegumChargeComponent>(actor);
        if (charge.Clone) { LeaveBlood(Transform(actor).Coordinates); QueueDel(actor); return; }
        if (!TryComp<BitrunBubblegumComponent>(actor, out var boss)) return;
        BloodAttack((actor, boss));
        boss.NextSequence = _timing.CurTime + TimeSpan.FromSeconds(0.05);
        if (boss.Remaining == 0) boss.NextAttack = _timing.CurTime + TimeSpan.FromSeconds(boss.Attack == BitrunBubblegumAttack.TripleCharge ? 1.5 : 2);
        _blocker.UpdateCanMove(actor);
    }

    private void ProcessEvent(Entity<BitrunBubblegumComponent> boss, BubblegumEvent ev)
    {
        if (ev.Kind == "warp" && ev.Target is {} target && !Deleted(target))
        {
            var pools = WarpDestinations(target);
            if (pools.Length == 0) return;
            _transform.SetCoordinates(boss, _random.Pick(pools));
            if (boss.Comp.EnrageUntil + TimeSpan.FromSeconds(boss.Comp.EnrageDuration * 2) <= _timing.CurTime)
            {
                boss.Comp.EnrageUntil = _timing.CurTime + TimeSpan.FromSeconds(boss.Comp.EnrageDuration);
                _speed.RefreshMovementSpeedModifiers(boss);
            }
            return;
        }
        if (ev.Kind == "devour" && ev.Target is {} victim && !Deleted(victim))
        {
            _megafauna.Devour(boss, victim);
            return;
        }
        foreach (var mob in _hazards.Occupants(ev.Position).ToArray())
        {
            if (mob.Owner == boss.Owner || HasComp<BitrunMegafaunaComponent>(mob)) continue;
            if (ev.Kind == "smack") _hazards.DirectDamage(mob, 10, "Blunt", boss, penetration: 0.4f);
            else if (ev.Kind == "grab" && !_mobs.IsAlive(mob))
            {
                _transform.SetCoordinates(mob, _hazards.Center(Transform(boss).Coordinates).Offset(Vector2.UnitY));
                boss.Comp.Events.Add(new(_timing.CurTime + TimeSpan.FromSeconds(0.2), "devour", default, mob));
            }
        }
    }

    public override void Update(float frameTime)
    {
        var charges = EntityQueryEnumerator<BitrunBubblegumChargeComponent>();
        while (charges.MoveNext(out var uid, out var charge))
        {
            if (TerminatingOrDeleted(charge.Caster) || _mobs.IsDead(charge.Caster)) { FinishCharge(uid, charge); continue; }
            if (charge.NextStep > _timing.CurTime) continue;
            charge.NextStep += TimeSpan.FromSeconds(0.05);
            var origin = _hazards.Center(Transform(uid).Coordinates);
            if (origin.EntityId != charge.Destination.EntityId || ++charge.Steps > 50 || _hazards.SameTile(origin, charge.Destination)) { FinishCharge(uid, charge); continue; }
            var delta = charge.Destination.Position - origin.Position;
            var next = origin.Offset(new Vector2(Math.Sign(delta.X), Math.Sign(delta.Y)));
            // The source destroys nearby dense structures and minerals while charging.
            _hazards.Mine(next, charge.Caster);
            foreach (var obstacle in _lookup.GetEntitiesInRange<DamageableComponent>(next, 1.5f).ToArray())
                if (!HasComp<MobStateComponent>(obstacle) && Transform(obstacle).Anchored)
                    _damage.TryChangeDamage(obstacle, new DamageSpecifier { DamageDict = { ["Blunt"] = 400, ["Structural"] = 400 } }, origin: charge.Caster);
            if (_hazards.Blocked(next)) { FinishCharge(uid, charge); continue; }
            _transform.SetCoordinates(uid, next); LeaveBlood(next);
            foreach (var mob in _hazards.Occupants(next).ToArray())
                if (mob.Owner != charge.Caster && !HasComp<BitrunMegafaunaComponent>(mob) && charge.Hit.Add(mob))
                    _hazards.DirectDamage(mob, charge.Clone ? 15 : 30, "Blunt", charge.Caster);
        }
        var bosses = EntityQueryEnumerator<BitrunBubblegumComponent, HTNComponent>();
        while (bosses.MoveNext(out var uid, out var comp, out var htn))
        {
            if (_mobs.IsDead(uid)) continue;
            var boss = new Entity<BitrunBubblegumComponent>(uid, comp);
            var tile = _hazards.Center(Transform(uid).Coordinates);
            if (comp.LastBloodTile is not {} previous || !_hazards.SameTile(previous, tile)) { LeaveBlood(tile); comp.LastBloodTile = tile; }
            var enraged = comp.EnrageUntil > _timing.CurTime;
            if (enraged != comp.WasEnraged)
            {
                comp.WasEnraged = enraged; _speed.RefreshMovementSpeedModifiers(uid);
                _appearance.SetData(uid, BitrunEquipmentVisuals.Mode, enraged ? "enraged" : "ground");
            }
            while (comp.Events.Count > 0 && comp.Events[0].At <= _timing.CurTime)
            {
                var ev = comp.Events[0]; comp.Events.RemoveAt(0); ProcessEvent(boss, ev);
                comp.Events.Sort((a, b) => a.At.CompareTo(b.At));
            }
            if (comp.Remaining > 0 && !HasComp<BitrunBubblegumChargeComponent>(uid) && comp.NextSequence <= _timing.CurTime)
            {
                if (comp.Target is {} sequenceTarget && !Deleted(sequenceTarget)) Sequence(boss, sequenceTarget);
                else { comp.Remaining = 0; _blocker.UpdateCanMove(uid); }
            }
            if (!htn.Enabled || comp.NextAttack > _timing.CurTime || comp.Remaining > 0 || HasComp<BitrunBubblegumChargeComponent>(uid)) continue;
            if (!htn.Blackboard.TryGetValue<EntityUid>("Target", out var target, EntityManager) || Deleted(target) || _mobs.IsDead(target)) continue;
            if (!BloodAttack(boss) || _random.Prob((25 + comp.Anger) / 100)) BloodWarp(boss, target);
            StartAttack(boss, target, !comp.Smash ? BitrunBubblegumAttack.TripleCharge : _random.Prob((50 + comp.Anger) / 100) ? BitrunBubblegumAttack.HallucinationCharge : BitrunBubblegumAttack.Surround);
        }
    }
}

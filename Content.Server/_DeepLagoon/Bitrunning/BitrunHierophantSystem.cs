using System.Linq;
using System.Numerics;
using Content.Server.NPC.HTN;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.ActionBlocker;
using Content.Shared.Damage;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

public enum BitrunHierophantAttack : byte { Blink, Cross, Burst, Chaser, BlinkChase, CrossSpam, ChaserSwarm }

[RegisterComponent]
public sealed partial class BitrunHierophantArenaComponent : Component
{
    [DataField] public HashSet<Vector2i> Tiles = new();
}

[RegisterComponent]
public sealed partial class BitrunHierophantComponent : Component
{
    public EntityCoordinates Home;
    public EntityUid? Target;
    public float Rage;
    public int BurstRange = 3;
    public int BeamRange = 5;
    public float ChaserDelay = 0.3f;
    public TimeSpan NextAttack;
    public TimeSpan NextChaser;
    public TimeSpan NextArena;
    public TimeSpan BusyUntil;
    public TimeSpan NoTargetSince;
    public bool Rooted;
    public bool BeganFight;
    public bool SavedCollision;
    public readonly List<HierophantEvent> Events = new();
}

public sealed record HierophantEvent(TimeSpan At, string Kind, EntityCoordinates Position, EntityUid? Target = null, int Direction = -1);

[RegisterComponent]
public sealed partial class BitrunHierophantChaserComponent : Component
{
    public EntityUid? Caster;
    public EntityUid Target;
    public TimeSpan NextStep;
    public TimeSpan End;
    public float Delay;
    public int Direction;
    public int Previous = -1;
    public int BeforePrevious = -1;
    public int Steps;
}

[RegisterComponent]
public sealed partial class BitrunHierophantBurstComponent : Component
{
    public EntityUid? Caster;
    public int Range;
    public int Ring;
    public float Spread = 0.5f;
    public TimeSpan NextRing;
}

/// <summary>Source hierophant.dm: independent delayed blasts, chasers, rage and blink sequences.</summary>
public sealed class BitrunHierophantSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private MobStateSystem _mobs = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private BitrunBossHazardSystem _hazards = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private Content.Shared.Maps.TurfSystem _turf = default!;
    private static readonly Vector2[] Cardinals = { Vector2.UnitX, Vector2.UnitY, -Vector2.UnitX, -Vector2.UnitY };

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunHierophantComponent, MapInitEvent>((Entity<BitrunHierophantComponent> ent, ref MapInitEvent args) => ent.Comp.Home = _hazards.Center(Transform(ent).Coordinates));
        SubscribeLocalEvent<BitrunHierophantComponent, DamageChangedEvent>((Entity<BitrunHierophantComponent> ent, ref DamageChangedEvent args) => CalculateRage(ent));
        SubscribeLocalEvent<BitrunHierophantComponent, UpdateCanMoveEvent>((Entity<BitrunHierophantComponent> ent, ref UpdateCanMoveEvent args) =>
        {
            if (ent.Comp.Rooted) args.Cancel();
        });
        SubscribeLocalEvent<BitrunHierophantComponent, MeleeHitEvent>((Entity<BitrunHierophantComponent> ent, ref MeleeHitEvent args) =>
        {
            if (!args.IsHit || args.HitEntities.Count == 0 || ent.Comp.Rooted) return;
            args.Handled = true;
            Burst(Transform(ent).Coordinates, ent, ent.Comp.NextAttack > _timing.CurTime ? 3 : ent.Comp.BurstRange,
                ent.Comp.NextAttack > _timing.CurTime ? 0.25f : 0.5f);
        });
        SubscribeLocalEvent<BitrunHierophantComponent, MobStateChangedEvent>((Entity<BitrunHierophantComponent> ent, ref MobStateChangedEvent args) =>
        {
            if (args.NewMobState == MobState.Dead) Burst(Transform(ent).Coordinates, null, 10);
        }, before: [typeof(BitrunMegafaunaSystem)]);
    }

    public void CalculateRage(Entity<BitrunHierophantComponent> boss)
    {
        boss.Comp.Rage = Math.Clamp((float)(CompOrNull<DamageableComponent>(boss)?.TotalDamage ?? 0) / 42, 0, 50);
        boss.Comp.BurstRange = 3 + (int)MathF.Round(boss.Comp.Rage * 0.08f, MidpointRounding.AwayFromZero);
        boss.Comp.BeamRange = 5 + (int)MathF.Round(boss.Comp.Rage * 0.12f, MidpointRounding.AwayFromZero);
    }

    private void Root(Entity<BitrunHierophantComponent> boss, bool value)
    {
        if (boss.Comp.Rooted == value) return;
        boss.Comp.Rooted = value;
        _blocker.UpdateCanMove(boss);
    }

    private bool InOriginalArena(EntityCoordinates position) => _turf.TryGetTileRef(position, out var tile) &&
        TryComp<BitrunHierophantArenaComponent>(tile.Value.GridUid, out var arena) && arena.Tiles.Contains(tile.Value.GridIndices);

    public bool Arena(Entity<BitrunHierophantComponent> boss, EntityUid target)
    {
        if (boss.Comp.NextArena > _timing.CurTime || Deleted(target) || _mobs.IsDead(target)) return false;
        if (target != boss.Owner && (InOriginalArena(Transform(boss).Coordinates) || InOriginalArena(Transform(target).Coordinates))) return false;
        boss.Comp.NextArena = _timing.CurTime + TimeSpan.FromSeconds(20);
        var center = _hazards.Center(Transform(target).Coordinates);
        foreach (var tile in BitrunBossGeometry.Ring(center, 11))
        {
            _hazards.Barrier(tile, boss, 10, true);
            Blast(tile, boss);
        }
        if (BitrunBossGeometry.Chebyshev(_transform.GetMapCoordinates(boss), _transform.ToMapCoordinates(center)) >= 11)
            Blink(boss, center);
        return true;
    }

    public EntityUid Burst(EntityCoordinates center, EntityUid? caster, int range, float spread = 0.5f)
    {
        var uid = Spawn(null, _hazards.Center(center));
        var burst = AddComp<BitrunHierophantBurstComponent>(uid);
        burst.Caster = caster; burst.Range = range; burst.Spread = spread; burst.NextRing = _timing.CurTime;
        return uid;
    }

    private void Blast(EntityCoordinates cell, EntityUid? caster, float damage = 10) =>
        _hazards.Telegraph(cell, caster, "BitrunHierophantBlast", 0.6f, damage,
            armorPenetration: 0.5f, mine: true, monsterBoost: true);

    private void Cross(Entity<BitrunHierophantComponent> boss, EntityCoordinates center, bool diagonal, bool all = false)
    {
        Spawn("BitrunHierophantTelegraph", center);
        boss.Comp.Events.Add(new(_timing.CurTime + TimeSpan.FromSeconds(0.2), all ? "cross-all" : diagonal ? "cross-diagonal" : "cross-cardinal", center));
    }

    private void Blink(Entity<BitrunHierophantComponent> boss, EntityCoordinates destination)
    {
        var origin = _hazards.Center(Transform(boss).Coordinates);
        destination = _hazards.Center(destination);
        Spawn("BitrunHierophantTelegraph", origin); Spawn("BitrunHierophantTelegraph", destination);
        void Add(float delay, string kind, EntityCoordinates at) => boss.Comp.Events.Add(new(_timing.CurTime + TimeSpan.FromSeconds(delay), kind, at));
        Add(0.2f, "blink-blast", origin); Add(0.2f, "blink-blast", destination);
        Add(0.3f, "fade-out", origin); Add(0.5f, "blink-move", destination); Add(0.7f, "fade-in", destination);
        boss.Comp.BusyUntil = Max(boss.Comp.BusyUntil, _timing.CurTime + TimeSpan.FromSeconds(0.8));
        Root(boss, true);
    }

    public bool StartAttack(Entity<BitrunHierophantComponent> boss, EntityUid target, BitrunHierophantAttack attack)
    {
        if (_mobs.IsDead(boss) || Deleted(target) || _mobs.IsDead(target) || boss.Comp.BusyUntil > _timing.CurTime ||
            boss.Comp.NextAttack > _timing.CurTime || Transform(boss).MapUid != Transform(target).MapUid) return false;
        CalculateRage(boss);
        boss.Comp.Target = target;
        var slowed = MathF.Max(1, 1 / MathF.Max(0.1f, CompOrNull<MovementSpeedModifierComponent>(target)?.SprintSpeedModifier ?? 1));
        boss.Comp.ChaserDelay = MathF.Max(1, 3 - boss.Comp.Rage * 0.04f + (slowed - 1) * 0.5f) / 10;
        boss.Comp.NextAttack = _timing.CurTime + TimeSpan.FromSeconds(MathF.Max(0.5f, 4 - boss.Comp.Rage * 0.075f));
        var center = _hazards.Center(Transform(target).Coordinates);
        var blinkCount = 1 + (int)MathF.Round(boss.Comp.Rage * 0.08f, MidpointRounding.AwayFromZero);
        var crossCount = 1 + (int)MathF.Round(boss.Comp.Rage * 0.12f, MidpointRounding.AwayFromZero);
        switch (attack)
        {
            case BitrunHierophantAttack.Blink: Blink(boss, center); break;
            case BitrunHierophantAttack.Cross:
                Cross(boss, center, _random.Prob(0.4f), boss.Comp.Rage > 0 && Comp<DamageableComponent>(boss).TotalDamage > 625 && _random.Prob(boss.Comp.Rage * 2 / slowed / 100));
                break;
            case BitrunHierophantAttack.Burst: Burst(Transform(boss).Coordinates, boss, boss.Comp.BurstRange); break;
            case BitrunHierophantAttack.Chaser:
                if (boss.Comp.NextChaser > _timing.CurTime) return false;
                SpawnChaser(boss, target, boss.Comp.ChaserDelay);
                boss.Comp.NextChaser = _timing.CurTime + TimeSpan.FromSeconds(10.1);
                if (_random.Prob(boss.Comp.Rage / 100) || BitrunBossGeometry.Chebyshev(_transform.GetMapCoordinates(boss), _transform.GetMapCoordinates(target)) <= 1)
                    SpawnChaser(boss, target, boss.Comp.ChaserDelay * 1.5f, _random.Next(4), 4);
                break;
            case BitrunHierophantAttack.BlinkChase:
                if (Comp<DamageableComponent>(boss).TotalDamage <= 625 || blinkCount <= 1) { Blink(boss, center); break; }
                Major(boss, 0.6f + blinkCount * (1.2f + slowed * 0.1f) + 0.8f);
                for (var i = 0; i < blinkCount; i++) boss.Comp.Events.Add(new(_timing.CurTime + TimeSpan.FromSeconds(0.6 + i * (1.2 + slowed * 0.1)), "blink-target", default, target));
                break;
            case BitrunHierophantAttack.CrossSpam:
                Major(boss, 0.6f + crossCount * (0.6f + slowed * 0.1f) + 0.8f);
                for (var i = 0; i < crossCount; i++) boss.Comp.Events.Add(new(_timing.CurTime + TimeSpan.FromSeconds(0.6 + i * (0.6 + slowed * 0.1)), "cross-target", default, target));
                break;
            case BitrunHierophantAttack.ChaserSwarm:
                if (boss.Comp.NextChaser > _timing.CurTime) return false;
                Major(boss, 0.6f + 4 * (0.8f + slowed * 0.1f) + 0.8f);
                var directions = Cardinals.Select((_, i) => i).OrderBy(_ => _random.Next()).ToArray();
                var targets = _lookup.GetEntitiesInRange<MobStateComponent>(Transform(boss).Coordinates, 21)
                    .Where(ent => ent.Owner != boss.Owner && HasComp<Robust.Shared.Player.ActorComponent>(ent) && _mobs.IsAlive(ent)).Select(ent => ent.Owner).ToArray();
                for (var i = 0; i < 4; i++) boss.Comp.Events.Add(new(_timing.CurTime + TimeSpan.FromSeconds(0.6 + i * (0.8 + slowed * 0.1)),
                    "chaser", default, targets.Length > 0 ? targets[i % targets.Length] : target, directions[i]));
                boss.Comp.NextChaser = _timing.CurTime + TimeSpan.FromSeconds(0.6 + 4 * (0.8 + slowed * 0.1) + 10.1);
                break;
        }
        Sort(boss);
        return true;
    }

    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first > second ? first : second;
    private static void Sort(Entity<BitrunHierophantComponent> boss) => boss.Comp.Events.Sort((a, b) => a.At.CompareTo(b.At));

    private void Major(Entity<BitrunHierophantComponent> boss, float duration)
    {
        Root(boss, true);
        boss.Comp.BusyUntil = _timing.CurTime + TimeSpan.FromSeconds(duration);
        boss.Comp.NextAttack = _timing.CurTime + TimeSpan.FromSeconds(MathF.Max(0.5f, 6 - boss.Comp.Rage * 0.075f));
    }

    private void SpawnChaser(Entity<BitrunHierophantComponent> boss, EntityUid target, float delay, int direction = 0, int steps = 0)
    {
        var uid = Spawn("BitrunHierophantChaser", _hazards.Center(Transform(boss).Coordinates));
        var chaser = AddComp<BitrunHierophantChaserComponent>(uid);
        chaser.Caster = boss; chaser.Target = target; chaser.Delay = delay; chaser.Direction = direction; chaser.Steps = steps;
        chaser.NextStep = _timing.CurTime + TimeSpan.FromSeconds(0.1); chaser.End = _timing.CurTime + TimeSpan.FromSeconds(9.8);
    }

    private void ProcessEvent(Entity<BitrunHierophantComponent> boss, HierophantEvent ev)
    {
        if (ev.Kind.StartsWith("cross-", StringComparison.Ordinal) && ev.Kind != "cross-target")
        {
            Blast(ev.Position, boss);
            for (var x = -1; x <= 1; x++)
            for (var y = -1; y <= 1; y++)
            {
                if (x == 0 && y == 0 || ev.Kind == "cross-cardinal" && x != 0 && y != 0 || ev.Kind == "cross-diagonal" && (x == 0 || y == 0)) continue;
                for (var i = 1; i <= boss.Comp.BeamRange; i++) Blast(ev.Position.Offset(new Vector2(x * i, y * i)), boss);
            }
        }
        else if (ev.Kind == "blink-blast") foreach (var tile in BitrunBossGeometry.Square(ev.Position, 1)) Blast(tile, boss, 30);
        else if (ev.Kind == "blink-move") _transform.SetCoordinates(boss, ev.Position);
        else if (ev.Kind == "fade-out")
        {
            if (TryComp<PhysicsComponent>(boss, out var body)) { boss.Comp.SavedCollision = body.CanCollide; _physics.SetCanCollide(boss, false, body: body); }
            _appearance.SetData(boss, BitrunEquipmentVisuals.Mode, "blink");
        }
        else if (ev.Kind == "fade-in")
        {
            if (TryComp<PhysicsComponent>(boss, out var body)) _physics.SetCanCollide(boss, boss.Comp.SavedCollision, body: body);
            _appearance.SetData(boss, BitrunEquipmentVisuals.Mode, "ground");
        }
        else if (ev.Target is {} target && !Deleted(target))
        {
            if (ev.Kind == "chaser") SpawnChaser(boss, target, boss.Comp.ChaserDelay, ev.Direction, 3);
            else if (ev.Kind == "blink-target") Blink(boss, Transform(target).Coordinates);
            else if (ev.Kind == "cross-target") Cross(boss, _hazards.Center(Transform(target).Coordinates), _random.Prob(0.4f));
        }
    }

    public override void Update(float frameTime)
    {
        var bursts = EntityQueryEnumerator<BitrunHierophantBurstComponent>();
        while (bursts.MoveNext(out var uid, out var burst))
        {
            if (burst.NextRing > _timing.CurTime) continue;
            foreach (var tile in BitrunBossGeometry.Ring(Transform(uid).Coordinates, burst.Ring)) Blast(tile, burst.Caster);
            if (++burst.Ring > burst.Range) { QueueDel(uid); continue; }
            burst.NextRing += TimeSpan.FromSeconds((1 + Math.Min(burst.Range - burst.Ring, 12) * burst.Spread) / 10);
        }
        var chasers = EntityQueryEnumerator<BitrunHierophantChaserComponent>();
        while (chasers.MoveNext(out var uid, out var chaser))
        {
            if (chaser.End <= _timing.CurTime || Deleted(chaser.Target) || Transform(chaser.Target).MapUid != Transform(uid).MapUid) { QueueDel(uid); continue; }
            if (chaser.NextStep > _timing.CurTime) continue;
            chaser.NextStep += TimeSpan.FromSeconds(chaser.Delay);
            var position = _hazards.Center(Transform(uid).Coordinates);
            var target = _hazards.Center(Transform(chaser.Target).Coordinates);
            if (position.EntityId != target.EntityId) { QueueDel(uid); continue; }
            if (chaser.Steps <= 0)
            {
                chaser.BeforePrevious = chaser.Previous; chaser.Previous = chaser.Direction;
                var delta = target.Position - position.Position;
                var desired = Math.Abs(delta.X) > Math.Abs(delta.Y) ? delta.X >= 0 ? 0 : 2 : delta.Y >= 0 ? 1 : 3;
                var oscillating = desired != chaser.Previous && desired == chaser.BeforePrevious || delta.LengthSquared() < 0.01f;
                chaser.Direction = oscillating ? _random.Pick(Enumerable.Range(0, 4).Where(dir => dir != chaser.BeforePrevious).ToArray()) : desired;
                chaser.Steps = oscillating ? 1 : 4;
            }
            position = position.Offset(Cardinals[chaser.Direction]);
            _transform.SetCoordinates(uid, position); chaser.Steps--;
            Blast(position, chaser.Caster);
        }
        var bosses = EntityQueryEnumerator<BitrunHierophantComponent, HTNComponent>();
        while (bosses.MoveNext(out var uid, out var comp, out var htn))
        {
            if (_mobs.IsDead(uid)) { comp.Events.Clear(); continue; }
            var boss = new Entity<BitrunHierophantComponent>(uid, comp);
            while (comp.Events.Count > 0 && comp.Events[0].At <= _timing.CurTime)
            {
                var ev = comp.Events[0]; comp.Events.RemoveAt(0); ProcessEvent(boss, ev); Sort(boss);
            }
            Root(boss, comp.BusyUntil > _timing.CurTime);
            if (!htn.Enabled) continue;
            if (!htn.Blackboard.TryGetValue<EntityUid>("Target", out var target, EntityManager) || Deleted(target) || _mobs.IsDead(target))
            {
                if (comp.NoTargetSince == default) comp.NoTargetSince = _timing.CurTime;
                if (comp.BeganFight && !comp.Rooted && comp.NoTargetSince + TimeSpan.FromSeconds(30) <= _timing.CurTime)
                {
                    Blink(boss, comp.Home); Sort(boss);
                    BitrunBossGeometry.Heal(_damage, uid, Comp<DamageableComponent>(uid), MathF.Max(250, Comp<DamageableComponent>(uid).TotalDamage.Float() / 2));
                    comp.BeganFight = false;
                }
                continue;
            }
            comp.NoTargetSince = default;
            if (!comp.BeganFight) { comp.BeganFight = true; Arena(boss, uid); }
            if (comp.Rooted || comp.NextAttack > _timing.CurTime) continue;
            Arena(boss, target);
            var distance = BitrunBossGeometry.Chebyshev(_transform.GetMapCoordinates(uid), _transform.GetMapCoordinates(target));
            CalculateRage(boss);
            BitrunHierophantAttack attack;
            if (_random.Prob(comp.Rage * 0.75f / 100))
            {
                var choices = new List<BitrunHierophantAttack>();
                if (1 + MathF.Round(comp.Rage * 0.12f) > 1) choices.Add(BitrunHierophantAttack.CrossSpam);
                if (distance > 2) choices.Add(BitrunHierophantAttack.BlinkChase);
                if (comp.NextChaser <= _timing.CurTime)
                {
                    if (_random.Prob(comp.Rage * 2 / 100)) choices.Clear();
                    choices.Add(BitrunHierophantAttack.ChaserSwarm);
                }
                attack = choices.Count > 0 ? _random.Pick(choices) : BitrunHierophantAttack.Burst;
            }
            else if (comp.NextChaser <= _timing.CurTime) attack = BitrunHierophantAttack.Chaser;
            else if (distance > 2 && _random.Prob((10 + comp.Rage * 0.5f) / 100)) attack = BitrunHierophantAttack.Blink;
            else attack = _random.Prob((70 - comp.Rage) / 100) ? BitrunHierophantAttack.Cross : BitrunHierophantAttack.Burst;
            StartAttack(boss, target, attack);
        }
    }
}

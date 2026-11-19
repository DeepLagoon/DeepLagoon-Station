using System.Numerics;
using Content.Server.NPC.HTN;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

public enum BitrunColossusAttack : byte { Spiral, Random, Shotgun, Alternating, Finale }

[RegisterComponent]
public sealed partial class BitrunColossusComponent : Component
{
    [DataField] public float MaxHealth = 1250;
    public bool Enraged;
    public bool FinaleUsed;
    public TimeSpan NextAttack;
    public EntityUid? Target;
    public readonly List<ColossusShot> Shots = new();
    public int FiredShots;
}

public readonly record struct ColossusShot(TimeSpan At, double Angle, bool AimAtTarget = false);

/// <summary>
/// Colossus callbacks from megafauna/colossus.dm and actions/mobs/projectileattack.dm.
/// The virtual-domain health multiplier applies before the source rage calculations.
/// </summary>
public sealed class BitrunColossusSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedGunSystem _guns = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private MobStateSystem _mobs = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunColossusComponent, DamageChangedEvent>(OnDamage);
    }

    private void OnDamage(Entity<BitrunColossusComponent> ent, ref DamageChangedEvent args)
    {
        ent.Comp.Enraged = Health(ent) <= ent.Comp.MaxHealth / 3;
    }

    private float Health(Entity<BitrunColossusComponent> ent) =>
        ent.Comp.MaxHealth - (float)(CompOrNull<DamageableComponent>(ent)?.TotalDamage ?? 0);

    public bool StartAttack(Entity<BitrunColossusComponent> boss, EntityUid target, BitrunColossusAttack attack)
    {
        if (_mobs.IsDead(boss) || Deleted(target) || _mobs.IsDead(target) ||
            boss.Comp.Shots.Count > 0 || boss.Comp.NextAttack > _timing.CurTime ||
            attack == BitrunColossusAttack.Finale && boss.Comp.FinaleUsed)
            return false;
        boss.Comp.Target = target;
        boss.Comp.Enraged = Health(boss) <= boss.Comp.MaxHealth / 3;
        var start = _timing.CurTime + TimeSpan.FromSeconds(1.5);
        void Add(float delay, double angle, bool aim = false) => boss.Comp.Shots.Add(new(start + TimeSpan.FromSeconds(delay), angle, aim));
        void RandomVolley(float delay)
        {
            for (var i = 0; i < 32; i++) Add(delay, _random.NextDouble() * 360);
        }
        void Shotgun(float delay)
        {
            foreach (var angle in new[] {12.5, 7.5, 2.5, -2.5, -7.5, -12.5}) Add(delay, angle, true);
        }
        void Alternating(float delay)
        {
            for (var wave = 0; wave < 4; wave++)
            for (var direction = 0; direction < 4; direction++)
                Add(delay + wave, direction * 90 + (wave % 2 == 0 ? 45 : 0));
        }
        switch (attack)
        {
            case BitrunColossusAttack.Spiral:
                var negative = _random.Prob(0.5f);
                // 80 bolts, one every decisecond. Below 1/3 HP both spirals run
                // concurrently after the source's additional one-second warning.
                for (var i = 0; i < 80; i++)
                {
                    Add(i * 0.1f + (boss.Comp.Enraged ? 1 : 0), (negative && !boss.Comp.Enraged ? 7 - i : 9 + i) * 22.5);
                    if (boss.Comp.Enraged) Add(i * 0.1f + 1, (7 - i) * 22.5);
                }
                break;
            case BitrunColossusAttack.Random: RandomVolley(0); break;
            case BitrunColossusAttack.Shotgun: Shotgun(0); break;
            case BitrunColossusAttack.Alternating: Alternating(0); break;
            case BitrunColossusAttack.Finale:
                boss.Comp.FinaleUsed = true;
                var counter = 10;
                var delay = 0f;
                for (var wave = 0; wave < 20; wave++)
                {
                    if (counter > 4) Shotgun(delay);
                    if (counter > 1) counter--;
                    // The original samples each tile in the 25x25 square.
                    for (var x = -12; x <= 12; x++)
                    for (var y = -12; y <= 12; y++)
                        if ((x != 0 || y != 0) && _random.Prob(Math.Min(counter, 2) / 100f))
                            Add(delay, Math.Atan2(y, x) * 180 / Math.PI);
                    delay += (counter + 1) / 10f;
                }
                for (var i = 0; i < 3; i++)
                {
                    RandomVolley(delay);
                    counter += 6;
                    delay += counter / 10f;
                }
                for (var i = 0; i < 3; i++) Alternating(delay + i * 4);
                break;
        }
        boss.Comp.Shots.Sort((a, b) => a.At.CompareTo(b.At));
        var end = boss.Comp.Shots.Count > 0 ? boss.Comp.Shots[^1].At : start;
        var recovery = attack switch
        {
            BitrunColossusAttack.Shotgun => 0.5,
            BitrunColossusAttack.Alternating => 2.5,
            BitrunColossusAttack.Spiral => 1.6, // final 0.1-second loop sleep, then 1.5-second cooldown
            BitrunColossusAttack.Finale => 3.5, // final one-second sleep, then 2.5-second cooldown
            _ => 1.5,
        };
        boss.Comp.NextAttack = end + TimeSpan.FromSeconds(recovery);
        return true;
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<BitrunColossusComponent, HTNComponent>();
        while (query.MoveNext(out var uid, out var comp, out var htn))
        {
            if (_mobs.IsDead(uid))
            {
                comp.Shots.Clear();
                continue;
            }
            while (comp.Shots.Count > 0 && comp.Shots[0].At <= _timing.CurTime)
            {
                var shot = comp.Shots[0];
                comp.Shots.RemoveAt(0);
                if (comp.Target is not { } target || Deleted(target) || _mobs.IsDead(target) ||
                    Transform(target).MapUid != Transform(uid).MapUid)
                {
                    comp.Shots.Clear();
                    break;
                }
                var angle = shot.Angle * Math.PI / 180;
                if (shot.AimAtTarget)
                {
                    var delta = _transform.GetWorldPosition(target) - _transform.GetWorldPosition(uid);
                    angle += Math.Atan2(delta.Y, delta.X);
                }
                var projectile = Spawn("BitrunColossusBolt", Transform(uid).Coordinates);
                _guns.ShootProjectile(projectile, new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)), Vector2.Zero, uid, uid, 5);
                comp.FiredShots++;
            }
            if (!htn.Enabled || comp.NextAttack > _timing.CurTime || comp.Shots.Count > 0 ||
                !htn.Blackboard.TryGetValue<EntityUid>("Target", out var current, EntityManager))
                continue;
            var health = Health((uid, comp));
            var anger = Math.Clamp((comp.MaxHealth - health) / 40, 0, 20);
            var attack = health <= comp.MaxHealth / 10 && !comp.FinaleUsed ? BitrunColossusAttack.Finale :
                _random.Prob((20 + anger) / 100f) ? BitrunColossusAttack.Spiral :
                _random.Prob(0.2f) ? BitrunColossusAttack.Random :
                _random.Prob((60 + anger) / 100f) ? BitrunColossusAttack.Shotgun : BitrunColossusAttack.Alternating;
            StartAttack((uid, comp), current, attack);
        }
    }
}

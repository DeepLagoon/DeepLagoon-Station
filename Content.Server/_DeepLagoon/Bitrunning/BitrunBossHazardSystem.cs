using System.Linq;
using System.Numerics;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Gatherable;
using Content.Server.Gatherable.Components;
using Content.Shared.Armor;
using Content.Shared.Damage;
using Content.Shared.Gravity;
using Content.Shared.Maps;
using Content.Shared.Mining.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Physics;
using Content.Shared.Projectiles;
using Content.Shared.Throwing;
using Robust.Shared.Map;
using Robust.Shared.Physics.Events;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunBossHazardComponent : Component
{
    public EntityUid? Caster;
    public TimeSpan At;
    public TimeSpan End;
    public float Damage;
    public string DamageType = "Heat";
    public float ArmorPenetration;
    public bool Active;
    public bool Mine;
    public bool Hotspot;
    public bool MonsterBoost;
    public float LavaDuration;
    public HashSet<EntityUid> Hit = new();
}

[RegisterComponent]
public sealed partial class BitrunBossBarrierComponent : Component
{
    public EntityUid? Caster;
    public bool CasterPasses;
    public TimeSpan End;
}

[RegisterComponent]
public sealed partial class BitrunTemporaryLavaComponent : Component
{
    public EntityUid? Caster;
    public TimeSpan End;
    public TimeSpan NextDamage;
}

/// <summary>Shared source telegraphs: damage occurs on the warned tile, including late crossers.</summary>
public sealed class BitrunBossHazardSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private TurfSystem _turf = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private MobStateSystem _mobs = default!;
    [Dependency] private GatherableSystem _gather = default!;
    [Dependency] private AtmosphereSystem _atmos = default!;
    [Dependency] private SharedGravitySystem _gravity = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunBossBarrierComponent, PreventCollideEvent>((Entity<BitrunBossBarrierComponent> wall, ref PreventCollideEvent args) =>
        {
            if (wall.Comp.CasterPasses && (args.OtherEntity == wall.Comp.Caster ||
                TryComp<ProjectileComponent>(args.OtherEntity, out var projectile) && projectile.Shooter == wall.Comp.Caster))
                args.Cancelled = true;
        });
    }

    public EntityCoordinates Center(EntityCoordinates coordinates) =>
        _turf.TryGetTileRef(coordinates, out var tile) ? _turf.GetTileCenter(tile.Value) : coordinates;

    public bool SameTile(EntityCoordinates first, EntityCoordinates second)
    {
        if (_turf.TryGetTileRef(first, out var a) && _turf.TryGetTileRef(second, out var b))
            return a.Value.GridUid == b.Value.GridUid && a.Value.GridIndices == b.Value.GridIndices;
        var left = _transform.ToMapCoordinates(first);
        var right = _transform.ToMapCoordinates(second);
        return left.MapId == right.MapId && MathF.Floor(left.Position.X) == MathF.Floor(right.Position.X) &&
            MathF.Floor(left.Position.Y) == MathF.Floor(right.Position.Y);
    }

    public bool Blocked(EntityCoordinates coordinates) => !_turf.TryGetTileRef(coordinates, out var tile) ||
        _turf.IsTileBlocked(tile.Value, CollisionGroup.Impassable);

    public EntityUid Telegraph(EntityCoordinates coordinates, EntityUid? caster, string prototype, float delay,
        float damage = 10, string damageType = "Heat", float armorPenetration = 0, bool ignoreArmor = false,
        bool mine = false, bool hotspot = false, bool monsterBoost = false, float lavaDuration = 0,
        HashSet<EntityUid>? hits = null, float window = 0.13f)
    {
        coordinates = Center(coordinates);
        var uid = Spawn(prototype, coordinates);
        var hazard = EnsureComp<BitrunBossHazardComponent>(uid);
        hazard.Caster = caster;
        hazard.At = _timing.CurTime + TimeSpan.FromSeconds(delay);
        hazard.End = hazard.At + TimeSpan.FromSeconds(window);
        hazard.Damage = damage;
        hazard.DamageType = damageType;
        hazard.ArmorPenetration = armorPenetration;
        hazard.Mine = mine;
        hazard.Hotspot = hotspot;
        hazard.MonsterBoost = monsterBoost;
        hazard.LavaDuration = lavaDuration;
        if (hits != null) hazard.Hit = hits;
        var armor = EnsureComp<DamageArmorOverrideComponent>(uid);
        armor.IgnoreArmor = ignoreArmor;
        armor.SourceArmorPenetration = true;
        if (!ignoreArmor) armor.ArmorType = "Blunt";
        if (mine) Mine(coordinates, caster);
        return uid;
    }

    public EntityUid Barrier(EntityCoordinates coordinates, EntityUid caster, float duration, bool hierophant)
    {
        var uid = Spawn(hierophant ? "BitrunHierophantBarrier" : "BitrunDrakeBarrier", Center(coordinates));
        var barrier = EnsureComp<BitrunBossBarrierComponent>(uid);
        barrier.Caster = caster;
        barrier.CasterPasses = hierophant;
        barrier.End = _timing.CurTime + TimeSpan.FromSeconds(duration);
        return uid;
    }

    public void Mine(EntityCoordinates coordinates, EntityUid? caster)
    {
        foreach (var rock in _lookup.GetEntitiesInRange<MiningScannerViewableComponent>(coordinates, 0.75f).ToArray())
            if (SameTile(Transform(rock).Coordinates, coordinates) && TryComp<GatherableComponent>(rock, out var gatherable) && !gatherable.Gathered)
                _gather.Gather(rock, caster ?? rock.Owner, gatherable);
    }

    public IEnumerable<Entity<MobStateComponent>> Occupants(EntityCoordinates coordinates) =>
        _lookup.GetEntitiesInRange<MobStateComponent>(coordinates, 0.75f).Where(mob => SameTile(Transform(mob).Coordinates, coordinates));

    public void DirectDamage(EntityUid target, float amount, string type, EntityUid? caster, EntityUid? tool = null, float penetration = 0, bool ignoreArmor = false)
    {
        EntityUid? overrideTool = null;
        if (ignoreArmor)
        {
            overrideTool = Spawn(null, Transform(target).Coordinates);
            AddComp<DamageArmorOverrideComponent>(overrideTool.Value).IgnoreArmor = true;
            tool = overrideTool;
        }
        _damage.TryChangeDamage(target, new DamageSpecifier { DamageDict = { [type] = amount } },
            origin: caster, tool: tool, armorPenetration: penetration);
        if (overrideTool != null) QueueDel(overrideTool.Value);
    }

    private void Activate(Entity<BitrunBossHazardComponent> hazard)
    {
        var origin = Transform(hazard).Coordinates;
        if (hazard.Comp.Hotspot && _turf.TryGetTileRef(origin, out var tile))
            _atmos.HotspotExpose(tile.Value.GridUid, tile.Value.GridIndices, 500, 50, hazard.Comp.Caster);
        if (hazard.Comp.LavaDuration > 0 && !Blocked(origin) &&
            !_lookup.GetEntitiesInRange<BitrunTemporaryLavaComponent>(origin, 0.75f).Any(lava => SameTile(Transform(lava).Coordinates, origin)))
        {
            var uid = Spawn("BitrunTemporaryLava", origin);
            var lava = EnsureComp<BitrunTemporaryLavaComponent>(uid);
            lava.Caster = hazard.Comp.Caster;
            lava.End = _timing.CurTime + TimeSpan.FromSeconds(hazard.Comp.LavaDuration);
        }
    }

    public override void Update(float frameTime)
    {
        var walls = EntityQueryEnumerator<BitrunBossBarrierComponent>();
        while (walls.MoveNext(out var uid, out var barrier))
            if (barrier.End <= _timing.CurTime) QueueDel(uid);
        var hazards = EntityQueryEnumerator<BitrunBossHazardComponent>();
        while (hazards.MoveNext(out var uid, out var hazard))
        {
            if (hazard.At > _timing.CurTime) continue;
            if (!hazard.Active)
            {
                hazard.Active = true;
                Activate((uid, hazard));
            }
            var origin = Transform(uid).Coordinates;
            foreach (var target in Occupants(origin))
            {
                if (target.Owner == hazard.Caster || _mobs.IsDead(target) || !hazard.Hit.Add(target)) continue;
                DirectDamage(target, hazard.Damage, hazard.DamageType, hazard.Caster, uid, hazard.ArmorPenetration);
                if (hazard.MonsterBoost && (HasComp<BitrunMegafaunaComponent>(target) || HasComp<BitrunHunterPreyComponent>(target)))
                    _damage.TryChangeDamage(target, new DamageSpecifier { DamageDict = { ["Blunt"] = hazard.Damage } }, true, origin: hazard.Caster);
            }
            if (hazard.End <= _timing.CurTime) QueueDel(uid);
        }
        var pools = EntityQueryEnumerator<BitrunTemporaryLavaComponent>();
        while (pools.MoveNext(out var uid, out var lava))
        {
            if (lava.End <= _timing.CurTime) { QueueDel(uid); continue; }
            if (lava.NextDamage > _timing.CurTime) continue;
            lava.NextDamage = _timing.CurTime + TimeSpan.FromSeconds(1);
            foreach (var target in Occupants(Transform(uid).Coordinates))
            {
                if (HasComp<BitrunDrakeComponent>(target) || HasComp<ThrownItemComponent>(target) || _gravity.IsWeightless(target.Owner)) continue;
                DirectDamage(target, 20, "Heat", lava.Caster, uid);
            }
        }
    }
}

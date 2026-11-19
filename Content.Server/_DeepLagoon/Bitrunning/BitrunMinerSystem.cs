using System.Numerics;
using Content.Server.NPC.HTN;
using Content.Shared.Damage;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Item;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.Mobs.Systems;
using Content.Shared.Toggleable;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Map;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunMinerComponent : Component
{
    public bool SawOpen;
    public TimeSpan NextAttack;
    public TimeSpan NextTransform;
    public TimeSpan NextDash;
    public EntityUid? PendingTarget;
    public TimeSpan FireAt;
    public int FiredShots;
}

/// <summary>Blood-drunk miner's source dash/accelerator sequence and transforming saw.</summary>
public sealed class BitrunMinerSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private TurfSystem _turf = default!;
    [Dependency] private ITileDefinitionManager _tiles = default!;
    [Dependency] private MobStateSystem _mobs = default!;
    [Dependency] private SharedGunSystem _guns = default!;
    [Dependency] private SharedAudioSystem _audio = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunMinerComponent, GetMeleeDamageEvent>((Entity<BitrunMinerComponent> ent, ref GetMeleeDamageEvent args) =>
        {
            args.Damage = new DamageSpecifier { DamageDict = new(args.Damage.DamageDict) };
            args.Damage.DamageDict["Slash"] = ent.Comp.SawOpen ? 10 : 6;
        });
        SubscribeLocalEvent<BitrunMinerComponent, GetMeleeAttackRateEvent>((Entity<BitrunMinerComponent> ent, ref GetMeleeAttackRateEvent args) => args.Rate = ent.Comp.SawOpen ? 1.5f : 2.5f);
    }

    public bool StartAttack(Entity<BitrunMinerComponent> miner, EntityUid target)
    {
        if (miner.Comp.NextAttack > _timing.CurTime || _mobs.IsDead(miner) || Deleted(target) || _mobs.IsDead(target) ||
            Transform(miner).MapUid != Transform(target).MapUid) return false;
        miner.Comp.NextAttack = _timing.CurTime + TimeSpan.FromSeconds(1.6);
        var delta = _transform.GetWorldPosition(target) - _transform.GetWorldPosition(miner);
        var dashed = false;
        if (Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y)) > 4 && miner.Comp.NextDash <= _timing.CurTime)
        {
            dashed = Dash(miner, target);
            miner.Comp.NextDash = _timing.CurTime + TimeSpan.FromSeconds(3);
        }
        miner.Comp.PendingTarget = target;
        miner.Comp.FireAt = _timing.CurTime + TimeSpan.FromSeconds(dashed ? 0.4 : 0);
        if (miner.Comp.NextTransform <= _timing.CurTime)
        {
            miner.Comp.SawOpen = !miner.Comp.SawOpen;
            miner.Comp.NextTransform = _timing.CurTime + TimeSpan.FromSeconds(5 + _random.NextFloat() * 5);
            _appearance.SetData(miner, ToggleableVisuals.Enabled, miner.Comp.SawOpen);
        }
        return true;
    }

    private bool Dash(EntityUid miner, EntityUid target)
    {
        var origin = Transform(miner).Coordinates;
        if (Transform(miner).GridUid == null) return false;
        var destination = _transform.GetWorldPosition(target);
        var candidates = new List<EntityCoordinates>();
        var best = 4f;
        // WhiteMoon searches the boundary of a four-tile square, then chooses
        // randomly among the closest reachable, non-lava, non-chasm tiles.
        for (var x = -4; x <= 4; x++)
        for (var y = -4; y <= 4; y++)
        {
            if (Math.Max(Math.Abs(x), Math.Abs(y)) != 4) continue;
            var candidate = origin.Offset(new Vector2(x, y));
            if (!_turf.TryGetTileRef(candidate, out var tile) || tile.Value.Tile.IsEmpty) continue;
            var id = _tiles[tile.Value.Tile.TypeId].ID;
            if (id.Contains("Lava", StringComparison.OrdinalIgnoreCase) || id.Contains("Chasm", StringComparison.OrdinalIgnoreCase)) continue;
            var map = _transform.ToMapCoordinates(candidate);
            var delta = destination - map.Position;
            var distance = Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y));
            if (distance > best || !_interaction.InRangeUnobstructed(_transform.GetMapCoordinates(miner), map, range: 6)) continue;
            if (distance < best) { best = distance; candidates.Clear(); }
            candidates.Add(candidate);
        }
        if (candidates.Count == 0) return false;
        _transform.SetCoordinates(miner, _random.Pick(candidates));
        return true;
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<BitrunMinerComponent, HTNComponent>();
        while (query.MoveNext(out var uid, out var comp, out var htn))
        {
            if (_mobs.IsDead(uid)) { comp.PendingTarget = null; continue; }
            if (comp.PendingTarget is { } target && comp.FireAt <= _timing.CurTime)
            {
                comp.PendingTarget = null;
                if (!Deleted(target) && !_mobs.IsDead(target) && Transform(target).MapUid == Transform(uid).MapUid)
                {
                    var direction = _transform.GetWorldPosition(target) - _transform.GetWorldPosition(uid);
                    if (direction.LengthSquared() > 0.001f)
                    {
                        var shot = Spawn("BitrunMinerKineticBolt", Transform(uid).Coordinates);
                        Comp<BitrunProjectileRangeComponent>(shot).Previous = _transform.GetMapCoordinates(shot);
                        _guns.ShootProjectile(shot, direction, Vector2.Zero, uid, uid, 11);
                        _audio.PlayPvs(new SoundPathSpecifier("/Audio/Weapons/Guns/Gunshots/kinetic_accel.ogg"), uid);
                        comp.FiredShots++;
                    }
                }
            }
            if (htn.Enabled && htn.Blackboard.TryGetValue<EntityUid>("Target", out var next, EntityManager)) StartAttack((uid, comp), next);
        }
    }
}

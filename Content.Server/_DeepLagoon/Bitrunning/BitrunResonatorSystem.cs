using System.Linq;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Gatherable;
using Content.Server.Gatherable.Components;
using Content.Shared.Damage;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Maps;
using Content.Shared.Mining.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.Popups;
using Content.Shared.StepTrigger.Systems;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Map;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunResonatorComponent : Component
{
    [DataField] public int FieldLimit = 4;
    [DataField] public float QuickBurstModifier = 0.8f;
    [DataField] public int SpreadFailureIncrement = 50;
    [DataField] public int Modes = 2;
    public int Mode;
}

[RegisterComponent]
public sealed partial class BitrunResonanceFieldComponent : Component
{
    public EntityUid? Resonator;
    public EntityUid? Creator;
    public TimeSpan BurstAt;
    public float DamageMultiplier = 1;
    public int FailureProbability;
    public bool Matrix;
    public bool Rupturing;
}

[RegisterComponent]
public sealed partial class BitrunResonanceSlowComponent : Component
{
    public TimeSpan Until;
    public bool Removing;
}

/// <summary>Explicit callbacks from mining/equipment/resonator.dm.</summary>
public sealed class BitrunResonatorSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private TurfSystem _turf = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private AtmosphereSystem _atmos = default!;
    [Dependency] private GatherableSystem _gather = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private MovementSpeedModifierSystem _movement = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunResonatorComponent, UseInHandEvent>((Entity<BitrunResonatorComponent> ent, ref UseInHandEvent args) =>
        {
            if (args.Handled) return;
            args.Handled = true;
            ent.Comp.Mode = (ent.Comp.Mode + 1) % ent.Comp.Modes;
            _popup.PopupEntity(Loc.GetString("bitrun-resonator-mode-" + ent.Comp.Mode), ent, args.User);
        });
        SubscribeLocalEvent<BitrunResonatorComponent, AfterInteractEvent>((Entity<BitrunResonatorComponent> ent, ref AfterInteractEvent args) =>
        {
            if (args.Handled || !_interaction.InRangeUnobstructed(_transform.GetMapCoordinates(args.User), _transform.ToMapCoordinates(args.ClickLocation), range: 1.5f)) return;
            args.Handled = CreateField(ent, args.ClickLocation, args.User);
        });
        SubscribeLocalEvent<BitrunResonatorComponent, MeleeHitEvent>((Entity<BitrunResonatorComponent> ent, ref MeleeHitEvent args) =>
        {
            if (args.IsHit && args.HitEntities.FirstOrDefault() is { Valid: true } target)
                CreateField(ent, Transform(target).Coordinates, args.User);
        });
        SubscribeLocalEvent<BitrunResonanceFieldComponent, StepTriggeredOnEvent>((Entity<BitrunResonanceFieldComponent> ent, ref StepTriggeredOnEvent args) =>
        {
            if (ent.Comp.Matrix) Burst(ent);
        });
        SubscribeLocalEvent<BitrunResonanceSlowComponent, RefreshMovementSpeedModifiersEvent>((Entity<BitrunResonanceSlowComponent> ent, ref RefreshMovementSpeedModifiersEvent args) =>
        {
            if (!ent.Comp.Removing) args.ModifySpeed(1 / 1.75f, 1 / 1.75f);
        });
    }

    private bool SameTile(EntityCoordinates a, EntityCoordinates b) =>
        _turf.TryGetTileRef(a, out var first) && _turf.TryGetTileRef(b, out var second) &&
        first.Value.GridUid == second.Value.GridUid && first.Value.GridIndices == second.Value.GridIndices;

    public bool CreateField(Entity<BitrunResonatorComponent> resonator, EntityCoordinates coordinates, EntityUid creator)
    {
        if (!_turf.TryGetTileRef(coordinates, out var tile) || tile.Value.Tile.IsEmpty) return false;
        foreach (var field in _lookup.GetEntitiesInRange<BitrunResonanceFieldComponent>(coordinates, 1.5f))
        {
            if (field.Comp.Rupturing || !SameTile(Transform(field).Coordinates, coordinates)) continue;
            field.Comp.DamageMultiplier = resonator.Comp.QuickBurstModifier;
            Burst(field);
            return true;
        }
        var count = 0;
        var query = EntityQueryEnumerator<BitrunResonanceFieldComponent>();
        while (query.MoveNext(out _, out var field))
            if (field.Resonator == resonator.Owner && !field.Rupturing) count++;
        if (count >= resonator.Comp.FieldLimit) return false;
        SpawnField(resonator, coordinates, creator, 0);
        return true;
    }

    private void SpawnField(Entity<BitrunResonatorComponent> resonator, EntityCoordinates coordinates, EntityUid? creator, int failure)
    {
        if (!_turf.TryGetTileRef(coordinates, out var tile)) return;
        var center = new EntityCoordinates(tile.Value.GridUid, (System.Numerics.Vector2)tile.Value.GridIndices + new System.Numerics.Vector2(0.5f, 0.5f));
        var uid = Spawn("BitrunResonanceField", center);
        var field = Comp<BitrunResonanceFieldComponent>(uid);
        field.Resonator = resonator;
        field.Creator = creator;
        field.Matrix = resonator.Comp.Mode == 2;
        field.FailureProbability = failure;
        field.BurstAt = _timing.CurTime + TimeSpan.FromSeconds(resonator.Comp.Mode == 0 ? 2 : 60);
    }

    public void Burst(Entity<BitrunResonanceFieldComponent> field)
    {
        if (field.Comp.Rupturing || TerminatingOrDeleted(field)) return;
        field.Comp.Rupturing = true;
        var origin = Transform(field).Coordinates;
        var air = _atmos.GetContainingMixture(field.Owner, true, true);
        var amount = (air is { Pressure: > 50 } ? 20 : 60) * field.Comp.DamageMultiplier;
        foreach (var target in _lookup.GetEntitiesInRange<DamageableComponent>(origin, 1.5f).ToArray())
        {
            if (!SameTile(Transform(target).Coordinates, origin)) continue;
            var damage = new DamageSpecifier();
            if (HasComp<MiningScannerViewableComponent>(target) && TryComp<GatherableComponent>(target, out var gatherable))
            {
                if (!gatherable.Gathered) _gather.Gather(target, field.Comp.Creator, gatherable);
                continue;
            }
            else if (HasComp<MobStateComponent>(target))
            {
                damage.DamageDict["Blunt"] = amount;
                var slow = EnsureComp<BitrunResonanceSlowComponent>(target);
                slow.Removing = false;
                slow.Until = _timing.CurTime + TimeSpan.FromSeconds(10);
                _movement.RefreshMovementSpeedModifiers(target);
            }
            else continue;
            _damage.TryChangeDamage(target, damage, origin: field.Comp.Creator);
        }
        foreach (var nearby in _lookup.GetEntitiesInRange<BitrunResonanceFieldComponent>(origin, 1.5f).ToArray())
            if (nearby.Owner != field.Owner && !nearby.Comp.Rupturing) Burst(nearby);
        if (field.Comp.Resonator is { } parent && TryComp<BitrunResonatorComponent>(parent, out var resonator) && !_random.Prob(field.Comp.FailureProbability / 100f))
        {
            foreach (var rock in _lookup.GetEntitiesInRange<OreVeinComponent>(origin, 1.5f).ToArray())
            {
                var position = Transform(rock).Coordinates;
                if (SameTile(position, origin) || _lookup.GetEntitiesInRange<BitrunResonanceFieldComponent>(position, 0.7f).Any(other => !other.Comp.Rupturing && SameTile(Transform(other).Coordinates, position))) continue;
                SpawnField((parent, resonator), position, field.Comp.Creator, field.Comp.FailureProbability + resonator.SpreadFailureIncrement);
            }
        }
        QueueDel(field);
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<BitrunResonanceFieldComponent>();
        var due = new List<Entity<BitrunResonanceFieldComponent>>();
        while (query.MoveNext(out var uid, out var field))
            if (!field.Rupturing && field.BurstAt <= _timing.CurTime) due.Add((uid, field));
        foreach (var field in due) Burst(field);
        var slows = EntityQueryEnumerator<BitrunResonanceSlowComponent>();
        while (slows.MoveNext(out var uid, out var slow))
        {
            if (slow.Removing || slow.Until > _timing.CurTime) continue;
            slow.Removing = true;
            _movement.RefreshMovementSpeedModifiers(uid);
            RemCompDeferred<BitrunResonanceSlowComponent>(uid);
        }
    }
}

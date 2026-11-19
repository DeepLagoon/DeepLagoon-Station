using System.Linq;
using System.Numerics;
using Content.Server.Atmos.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.Examine;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Projectiles;
using Content.Shared.Tools.Systems;
using Content.Shared.Verbs;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Containers;
using Robust.Shared.Map;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunKineticAcceleratorComponent : Component
{
    [DataField] public int Capacity = 100;
    [DataField] public float Recharge = 1.6f;
    [DataField] public float Range = 3;
}

[RegisterComponent]
public sealed partial class BitrunKineticModkitComponent : Component
{
    [DataField] public bool MinebotPassthrough;
    [DataField] public int Cost = 30;
    [DataField] public float Damage;
    [DataField] public float Range;
    [DataField] public float Cooldown;
}

/// <summary>Distance, rather than elapsed time, limits source projectiles.</summary>
[RegisterComponent]
public sealed partial class BitrunProjectileRangeComponent : Component
{
    [DataField] public bool MinebotPassthrough;
    [DataField] public float Range = 50;
    [DataField] public bool Kinetic;
    [DataField] public float Speed = 25;
    public MapCoordinates? Previous;
    public float Travelled;
}

public sealed class BitrunKineticSystem : EntitySystem
{
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedToolSystem _tools = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private AtmosphereSystem _atmos = default!;

    private const string ModContainer = "bitrun-kinetic-modkits";

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunKineticAcceleratorComponent, ComponentInit>((Entity<BitrunKineticAcceleratorComponent> ent, ref ComponentInit _) => Container(ent));
        SubscribeLocalEvent<BitrunKineticAcceleratorComponent, InteractUsingEvent>(OnInstall);
        SubscribeLocalEvent<BitrunKineticModkitComponent, InteractUsingEvent>(OnInstallReversed);
        SubscribeLocalEvent<BitrunKineticAcceleratorComponent, EntInsertedIntoContainerMessage>(OnInserted);
        SubscribeLocalEvent<BitrunKineticAcceleratorComponent, EntRemovedFromContainerMessage>(OnRemoved);
        SubscribeLocalEvent<BitrunKineticAcceleratorComponent, GetVerbsEvent<AlternativeVerb>>(OnRemoveVerbs);
        SubscribeLocalEvent<BitrunKineticAcceleratorComponent, ExaminedEvent>(OnExamine);
        SubscribeLocalEvent<BitrunKineticAcceleratorComponent, AmmoShotEvent>(OnShot);
        SubscribeLocalEvent<BitrunProjectileRangeComponent, ProjectileHitEvent>(OnHit);
    }

    private Container Container(EntityUid gun) => _containers.EnsureContainer<Container>(gun, ModContainer);

    public int UsedCapacity(EntityUid gun) => Container(gun).ContainedEntities
        .Sum(uid => CompOrNull<BitrunKineticModkitComponent>(uid)?.Cost ?? 0);

    public bool TryInstall(Entity<BitrunKineticAcceleratorComponent> gun, EntityUid kit, EntityUid user)
    {
        if (!TryComp<BitrunKineticModkitComponent>(kit, out var mod))
            return false;
        if (UsedCapacity(gun) + mod.Cost > gun.Comp.Capacity)
        {
            _popup.PopupEntity(Loc.GetString("bitrun-modkit-capacity"), gun, user);
            return false;
        }
        return _hands.TryDropIntoContainer(user, kit, Container(gun));
    }

    private void OnInstall(Entity<BitrunKineticAcceleratorComponent> gun, ref InteractUsingEvent args)
    {
        if (args.Handled)
            return;
        if (HasComp<BitrunKineticModkitComponent>(args.Used))
        {
            args.Handled = true;
            TryInstall(gun, args.Used, args.User);
        }
        else if (_tools.HasQuality(args.Used, "Prying"))
        {
            args.Handled = true;
            foreach (var kit in Container(gun).ContainedEntities.ToArray())
                Remove(gun, kit, args.User);
        }
    }

    private void OnInstallReversed(Entity<BitrunKineticModkitComponent> kit, ref InteractUsingEvent args)
    {
        if (!args.Handled && TryComp<BitrunKineticAcceleratorComponent>(args.Used, out var gun))
        {
            args.Handled = true;
            TryInstall((args.Used, gun), kit, args.User);
        }
    }

    public bool Remove(Entity<BitrunKineticAcceleratorComponent> gun, EntityUid kit, EntityUid user)
    {
        if (!Container(gun).Contains(kit) || !_containers.Remove(kit, Container(gun)))
            return false;
        _transform.SetCoordinates(kit, Transform(gun).Coordinates);
        _hands.TryPickupAnyHand(user, kit);
        Refresh(gun);
        return true;
    }

    private void OnRemoveVerbs(Entity<BitrunKineticAcceleratorComponent> gun, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;
        var user = args.User;
        foreach (var kit in Container(gun).ContainedEntities)
        {
            var selected = kit;
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("bitrun-modkit-remove", ("kit", Name(kit))),
                Act = () => Remove(gun, selected, user) });
        }
    }

    private void OnExamine(Entity<BitrunKineticAcceleratorComponent> gun, ref ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("bitrun-modkit-remaining", ("remaining", gun.Comp.Capacity - UsedCapacity(gun))));
        foreach (var kit in Container(gun).ContainedEntities)
            args.PushText(Name(kit));
    }

    private void OnInserted(Entity<BitrunKineticAcceleratorComponent> gun, ref EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID == ModContainer) Refresh(gun);
    }

    private void OnRemoved(Entity<BitrunKineticAcceleratorComponent> gun, ref EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID == ModContainer) Refresh(gun);
    }

    private void Refresh(Entity<BitrunKineticAcceleratorComponent> gun)
    {
        var reduction = Container(gun).ContainedEntities.Sum(uid => CompOrNull<BitrunKineticModkitComponent>(uid)?.Cooldown ?? 0);
        if (TryComp<RechargeBasicEntityAmmoComponent>(gun, out var recharge))
        {
            recharge.RechargeCooldown = Math.Max(0.01f, gun.Comp.Recharge - reduction);
            Dirty(gun, recharge);
        }
    }

    private void OnShot(Entity<BitrunKineticAcceleratorComponent> gun, ref AmmoShotEvent args)
    {
        var mods = Container(gun).ContainedEntities.Select(uid => CompOrNull<BitrunKineticModkitComponent>(uid)).Where(m => m != null).ToArray();
        foreach (var uid in args.FiredProjectiles)
        {
            var range = EnsureComp<BitrunProjectileRangeComponent>(uid);
            range.Range = gun.Comp.Range + mods.Sum(mod => mod!.Range);
            range.Kinetic = true;
            range.MinebotPassthrough = mods.Any(mod => mod!.MinebotPassthrough);
            range.Previous = _transform.GetMapCoordinates(uid);
            if (TryComp<ProjectileComponent>(uid, out var projectile))
            {
                projectile.Damage.DamageDict["Blunt"] += mods.Sum(mod => mod!.Damage);
                Dirty(uid, projectile);
            }
        }
    }

    private void OnHit(Entity<BitrunProjectileRangeComponent> projectile, ref ProjectileHitEvent args)
    {
        if (projectile.Comp.MinebotPassthrough && HasComp<BitrunMinebotComponent>(args.Target))
        {
            args.Handled = true;
            return;
        }
        if (!projectile.Comp.Kinetic)
            return;
        // WhiteMoon weakens kinetic blasts above 50 kPa, after applying modkits.
        var air = _atmos.GetContainingMixture(args.Target, true, true);
        if (air is { Pressure: > 50 })
            args.Damage *= 0.25f;
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<BitrunProjectileRangeComponent, ProjectileComponent>();
        while (query.MoveNext(out var uid, out var range, out _))
        {
            var current = _transform.GetMapCoordinates(uid);
            if (range.Previous is { } previous && previous.MapId == current.MapId)
                range.Travelled += Vector2.Distance(previous.Position, current.Position);
            range.Previous = current;
            if (range.Travelled >= range.Range)
                QueueDel(uid);
        }
    }
}

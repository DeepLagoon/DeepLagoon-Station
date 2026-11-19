using Content.Shared.Containers.ItemSlots;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Weapons.Ranged;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using System.Numerics;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunSourceGunComponent : Component
{
    [DataField] public bool ChamberConsumesMagazine;
    [DataField] public bool Initialized;
    [DataField] public bool ManualCycle;
    [DataField] public bool BoltOpen;
    [DataField] public bool NeedsCycle;
}

/// <summary>Source rules that are not provided by the native ammo providers.</summary>
public sealed class BitrunSourceGunSystem : EntitySystem
{
    [Dependency] private ItemSlotsSystem _slots = default!;
    [Dependency] private SharedGunSystem _guns = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunSourceGunComponent, MapInitEvent>(OnMapInit, after: [typeof(ItemSlotsSystem)]);
        SubscribeLocalEvent<BitrunSourceGunComponent, UseInHandEvent>(OnUse, before: [typeof(SharedGunSystem)]);
        SubscribeLocalEvent<BitrunSourceGunComponent, ShotAttemptedEvent>(OnAttempt);
        SubscribeLocalEvent<BitrunSourceGunComponent, AmmoShotEvent>(OnShot);
    }

    private void OnMapInit(Entity<BitrunSourceGunComponent> gun, ref MapInitEvent args)
    {
        if (gun.Comp.Initialized)
            return;
        gun.Comp.Initialized = true;
        if (gun.Comp.ChamberConsumesMagazine && _slots.TryGetSlot(gun, "gun_magazine", out var slot) &&
            slot.Item is { } magazine && TryComp<BallisticAmmoProviderComponent>(magazine, out var ammo))
        {
            // WhiteMoon chambers a round out of the starting magazine. Native
            // ItemSlots otherwise creates an extra round in the chamber.
            _guns.SetBallisticUnspawned((magazine, ammo), Math.Max(0, ammo.UnspawnedCount - 1));
        }
    }

    private void OnUse(Entity<BitrunSourceGunComponent> gun, ref UseInHandEvent args)
    {
        if (args.Handled || !gun.Comp.ManualCycle)
            return;
        gun.Comp.BoltOpen = !gun.Comp.BoltOpen;
        _appearance.SetData(gun, AmmoVisuals.BoltClosed, !gun.Comp.BoltOpen);
        if (!gun.Comp.BoltOpen)
        {
            gun.Comp.NeedsCycle = false;
            args.Handled = true;
        }
        // Opening delegates cartridge ejection to the native ballistic provider.
    }

    private void OnAttempt(Entity<BitrunSourceGunComponent> gun, ref ShotAttemptedEvent args)
    {
        if (gun.Comp.ManualCycle && (gun.Comp.BoltOpen || gun.Comp.NeedsCycle))
            args.Cancel();
    }

    private void OnShot(Entity<BitrunSourceGunComponent> gun, ref AmmoShotEvent args)
    {
        if (args.FiredProjectiles.Count == 0)
            return;
        if (gun.Comp.ManualCycle)
            gun.Comp.NeedsCycle = true;
        foreach (var uid in args.FiredProjectiles)
        {
            if (!TryComp<BitrunProjectileRangeComponent>(uid, out var range) ||
                !TryComp<PhysicsComponent>(uid, out var body) || body.LinearVelocity.LengthSquared() < 0.001f)
                continue;
            range.Previous = _transform.GetMapCoordinates(uid);
            _physics.SetLinearVelocity(uid, Vector2.Normalize(body.LinearVelocity) * range.Speed, body: body);
        }
    }
}

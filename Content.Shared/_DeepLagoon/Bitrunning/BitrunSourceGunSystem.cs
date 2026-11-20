using Content.Shared.Interaction.Events;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.Wieldable;
using Robust.Shared.GameStates;

namespace Content.Shared._DeepLagoon.Bitrunning;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(fieldDeltas: true)]
public sealed partial class BitrunSourceGunComponent : Component
{
    [DataField] public bool ChamberConsumesMagazine;
    [DataField] public bool Initialized;
    [DataField, AutoNetworkedField] public bool ManualCycle;
    [DataField, AutoNetworkedField] public bool BoltOpen;
    [DataField, AutoNetworkedField] public bool NeedsCycle;
}

/// <summary>Predict the same locking bolt and firing restrictions on both sides.</summary>
public sealed class SharedBitrunSourceGunSystem : EntitySystem
{
    [Dependency] private SharedAppearanceSystem _appearance = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunSourceGunComponent, UseInHandEvent>(OnUse,
            before: [typeof(SharedGunSystem)], after: [typeof(SharedWieldableSystem)]);
        SubscribeLocalEvent<BitrunSourceGunComponent, ShotAttemptedEvent>(OnAttempt);
        SubscribeLocalEvent<BitrunSourceGunComponent, GunShotEvent>(OnShot);
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
            // Only opening ejects the casing. Closing must not eject another live round.
            args.Handled = true;
        }
        Dirty(gun);
    }

    private void OnAttempt(Entity<BitrunSourceGunComponent> gun, ref ShotAttemptedEvent args)
    {
        if (gun.Comp.ManualCycle && (gun.Comp.BoltOpen || gun.Comp.NeedsCycle))
            args.Cancel();
    }

    private void OnShot(Entity<BitrunSourceGunComponent> gun, ref GunShotEvent args)
    {
        if (!gun.Comp.ManualCycle || args.Ammo.Count == 0)
            return;

        gun.Comp.NeedsCycle = true;
        DirtyField(gun, gun.Comp, nameof(BitrunSourceGunComponent.NeedsCycle));
    }
}

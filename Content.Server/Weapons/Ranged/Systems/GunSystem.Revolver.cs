using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Containers;

namespace Content.Server.Weapons.Ranged.Systems;

public sealed partial class GunSystem
{
    protected override void InitializeRevolver()
    {
        base.InitializeRevolver();
        SubscribeLocalEvent<RevolverAmmoProviderComponent, EntRemovedFromContainerMessage>(OnRevolverAmmoRemoved);
    }

    private void OnRevolverAmmoRemoved(EntityUid uid, RevolverAmmoProviderComponent component, EntRemovedFromContainerMessage args)
    {
        if (args.Container != component.AmmoContainer || TerminatingOrDeleted(uid))
            return;

        var index = component.AmmoSlots.IndexOf(args.Entity);
        if (index == -1)
            return;

        component.AmmoSlots[index] = null;
        component.Chambers[index] = null;
        UpdateRevolverAppearance(uid, component);
        Dirty(uid, component);
    }

    protected override void SpinRevolver(EntityUid revolverUid, RevolverAmmoProviderComponent component, EntityUid? user = null)
    {
        base.SpinRevolver(revolverUid, component, user);
        var index = Random.Next(component.Capacity);

        if (component.CurrentIndex == index)
            return;

        component.CurrentIndex = index;
        Dirty(revolverUid, component);
    }
}

using System.Numerics;
using Content.Server.Clothing.Systems;
using Content.Server.Explosion.EntitySystems;
using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunMirageGrenadeComponent : Component
{
    public EntityUid? Thrower;
}

[RegisterComponent]
public sealed partial class BitrunMirageComponent : Component;

public sealed class BitrunMirageSystem : EntitySystem
{
    [Dependency] private SharedHumanoidAppearanceSystem _humanoid = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private ChameleonClothingSystem _clothing = default!;
    [Dependency] private MetaDataSystem _meta = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunMirageGrenadeComponent, ActiveTimerTriggerEvent>(OnPrime);
        SubscribeLocalEvent<BitrunMirageGrenadeComponent, TriggerEvent>(OnTrigger);
        SubscribeLocalEvent<BitrunMirageComponent, MobStateChangedEvent>(OnDeath);
    }

    private void OnPrime(Entity<BitrunMirageGrenadeComponent> ent, ref ActiveTimerTriggerEvent args) => ent.Comp.Thrower = args.User;

    private void OnTrigger(Entity<BitrunMirageGrenadeComponent> ent, ref TriggerEvent args)
    {
        if (ent.Comp.Thrower is not { } original || Deleted(original))
            return;
        var copy = Spawn("BitrunMirage", Transform(ent).Coordinates);
        _meta.SetEntityName(copy, Name(original));
        if (HasComp<HumanoidAppearanceComponent>(original))
            _humanoid.CloneAppearance(original, copy);
        if (!TryComp<InventoryComponent>(original, out var inventory))
            return;
        foreach (var slot in inventory.Slots)
        {
            if (!_inventory.TryGetSlotEntity(original, slot.Name, out var item, inventory) ||
                _clothing.CaptureAppearance(item.Value) is not { } appearance)
                continue;
            // Visual replicas have no real item contents or powers to loot.
            var garment = Spawn("ModLinkProjectionGarment", new Robust.Shared.Map.EntityCoordinates(copy, Vector2.Zero));
            var projected = Comp<ModLinkProjectedClothingComponent>(garment);
            projected.Appearance = appearance;
            projected.Slot = slot.Name;
            projected.Species = inventory.SpeciesId;
            Dirty(garment, projected);
            if (!_inventory.TryEquip(copy, garment, slot.Name, silent: true, force: true))
                QueueDel(garment);
        }
    }

    private void OnDeath(Entity<BitrunMirageComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Dead)
            QueueDel(ent);
    }
}

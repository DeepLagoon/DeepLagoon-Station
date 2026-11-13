using System.Numerics;
using Content.Server.Clothing.Systems;
using Content.Shared._DeepLagoon.LustWish;
using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared._Goobstation.Clothing;
using Content.Shared.Clothing.Components;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Content.Shared.Sprite;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Modsuits;

public sealed partial class ModSystem
{
    [Dependency] private ChameleonClothingSystem _projectionClothing = default!;
    [Dependency] private SharedHumanoidAppearanceSystem _projectionHumanoid = default!;
    [Dependency] private SharedAppearanceSystem _modAppearance = default!;

    private readonly Dictionary<EntityUid, ProjectionAvatar> _projectionAvatars = new();

    private sealed class ProjectionAvatar(EntityUid entity)
    {
        public readonly EntityUid Entity = entity;
        public GameTick? BodyTick;
        public readonly Dictionary<string, ProjectedGarment> Garments = new();
    }

    private readonly record struct GarmentState(EntityUid Item, GameTick ClothingTick, GameTick PaletteTick, bool Sealed);
    private readonly record struct ProjectedGarment(EntityUid Replica, GarmentState State);

    private void InitializeProjection()
    {
        SubscribeLocalEvent<ModLinkProjectedClothingComponent, ContainerGettingRemovedAttemptEvent>(OnProjectionRemoveAttempt);
    }

    private void OnProjectionRemoveAttempt(Entity<ModLinkProjectedClothingComponent> garment, ref ContainerGettingRemovedAttemptEvent args)
    {
        // Inventory items exist only to render an outfit and may never be taken from the projection.
        args.Cancel();
    }

    private void SpawnProjectionAvatar(EntityUid hologram, EntityUid speaker)
    {
        if (!HasComp<HumanoidAppearanceComponent>(speaker))
            return;

        var avatar = Spawn("ModLinkProjectionAvatar", new EntityCoordinates(hologram, new Vector2(0, 0.22f)));
        _projectionAvatars.Add(speaker, new ProjectionAvatar(avatar));
        RefreshProjectionAvatar(speaker);
    }

    private void RefreshProjectionAvatar(EntityUid speaker)
    {
        if (!_projectionAvatars.TryGetValue(speaker, out var projection) ||
            !TryComp<HumanoidAppearanceComponent>(speaker, out var original) ||
            !TryComp<HumanoidAppearanceComponent>(projection.Entity, out var avatar))
            return;

        if (projection.BodyTick != original.LastModifiedTick)
        {
            _projectionHumanoid.CloneAppearance(speaker, projection.Entity, original, avatar);
            avatar.HiddenLayers = new(original.HiddenLayers);
            avatar.PermanentlyHidden = new(original.PermanentlyHidden);
            avatar.TattoosEnabled = original.TattoosEnabled;
            avatar.GeneticallyModified = original.GeneticallyModified;
            if (HasComp<ScaleVisualsComponent>(projection.Entity))
                _modAppearance.SetData(projection.Entity, ScaleVisuals.Scale, new Vector2(original.Width, original.Height));
            Dirty(projection.Entity, avatar);
            projection.BodyTick = original.LastModifiedTick;
        }

        if (!TryComp<InventoryComponent>(speaker, out var inventory))
            return;

        foreach (var slot in inventory.Slots)
            RefreshProjectedGarment(speaker, inventory, slot.Name, projection);
    }

    private void RefreshProjectedGarment(EntityUid speaker, InventoryComponent inventory, string slot, ProjectionAvatar projection)
    {
        var hasReplica = projection.Garments.TryGetValue(slot, out var previous);
        if (!_inventory.TryGetSlotEntity(speaker, slot, out var item, inventory) ||
            !TryComp<ClothingComponent>(item, out var clothing) ||
            clothing.InSlotFlag == null || (clothing.Slots & clothing.InSlotFlag.Value) == 0)
        {
            if (hasReplica)
                DeleteProjectedGarment(projection, slot, previous.Replica);

            return;
        }

        var paletteTick = TryComp<LustWishPaletteComponent>(item, out var palette) ? palette.LastModifiedTick : GameTick.Zero;
        var sealedPart = TryComp<AppearanceComponent>(item, out var appearance) &&
                         _modAppearance.TryGetData(item.Value, SealableClothingVisuals.Sealed, out bool sealedState, appearance) &&
                         sealedState;
        var state = new GarmentState(item.Value, clothing.LastModifiedTick, paletteTick, sealedPart);
        if (hasReplica && previous.State == state && Exists(previous.Replica))
            return;

        if (hasReplica)
            DeleteProjectedGarment(projection, slot, previous.Replica);

        if (!_inventory.TryGetSlot(projection.Entity, slot, out _) ||
            _projectionClothing.CaptureAppearance(item.Value) is not { } snapshot)
            return;

        var replica = Spawn("ModLinkProjectionGarment", new EntityCoordinates(projection.Entity, Vector2.Zero));
        var component = Comp<ModLinkProjectedClothingComponent>(replica);
        component.Appearance = snapshot;
        component.Slot = slot;
        component.Species = inventory.SpeciesId;
        Dirty(replica, component);
        _modAppearance.SetData(replica, SealableClothingVisuals.Sealed, sealedPart);

        if (_inventory.TryEquip(projection.Entity, replica, slot, silent: true, force: true))
            projection.Garments.Add(slot, new ProjectedGarment(replica, state));
        else
            QueueDel(replica);
    }

    private void DeleteProjectedGarment(ProjectionAvatar projection, string slot, EntityUid replica)
    {
        projection.Garments.Remove(slot);
        if (Exists(replica))
            Del(replica);
    }
}

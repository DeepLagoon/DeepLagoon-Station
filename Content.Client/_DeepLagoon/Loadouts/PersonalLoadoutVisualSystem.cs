using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.Clothing;
using Robust.Client.GameObjects;
using Content.Shared.Clothing.Components;
using Content.Client.Inventory;
using Robust.Shared.Containers;

namespace Content.Client._DeepLagoon.Loadouts;

public sealed partial class PersonalLoadoutVisualSystem : VisualizerSystem<PersonalLoadoutVisualsComponent>
{
    [Dependency] private SpriteSystem _sprites = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PersonalLoadoutVisualsComponent, EquipmentVisualsUpdatedEvent>(OnEquipped);
    }
    protected override void OnAppearanceChange(EntityUid uid, PersonalLoadoutVisualsComponent component, ref AppearanceChangeEvent args)
    {
        if (!AppearanceSystem.TryGetData<Color>(uid, PersonalLoadoutVisuals.Color, out var color, args.Component)) return;
        if (args.Sprite != null) _sprites.SetColor((uid, args.Sprite), color);
        if (TryComp<ClothingComponent>(uid, out var clothing) && clothing.InSlot is { } slot
            && _containers.TryGetContainingContainer((uid, null, null), out var container)
            && TryComp<InventorySlotsComponent>(container.Owner, out var inventory)
            && TryComp<SpriteComponent>(container.Owner, out var wearer)
            && inventory.VisualLayerKeys.TryGetValue(slot, out var layers))
            foreach (var key in layers) _sprites.LayerSetColor((container.Owner, wearer), key, color);
    }
    private void OnEquipped(EntityUid uid, PersonalLoadoutVisualsComponent component, EquipmentVisualsUpdatedEvent args)
    {
        if (!TryComp<SpriteComponent>(args.Equipee, out var sprite) || !AppearanceSystem.TryGetData<Color>(uid, PersonalLoadoutVisuals.Color, out var color))
            return;
        foreach (var key in args.RevealedLayers)
            _sprites.LayerSetColor((args.Equipee, sprite), key, color);
    }
}

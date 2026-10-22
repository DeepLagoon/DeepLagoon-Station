using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.Clothing;
using Robust.Client.GameObjects;

namespace Content.Client._DeepLagoon.Loadouts;

public sealed partial class PersonalLoadoutVisualSystem : VisualizerSystem<PersonalLoadoutVisualsComponent>
{
    [Dependency] private SpriteSystem _sprites = default!;
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PersonalLoadoutVisualsComponent, EquipmentVisualsUpdatedEvent>(OnEquipped);
    }
    protected override void OnAppearanceChange(EntityUid uid, PersonalLoadoutVisualsComponent component, ref AppearanceChangeEvent args)
    {
        if (args.Sprite != null && AppearanceSystem.TryGetData<Color>(uid, PersonalLoadoutVisuals.Color, out var color, args.Component))
            _sprites.SetColor((uid, args.Sprite), color);
    }
    private void OnEquipped(EntityUid uid, PersonalLoadoutVisualsComponent component, EquipmentVisualsUpdatedEvent args)
    {
        if (!TryComp<SpriteComponent>(args.Equipee, out var sprite) || !AppearanceSystem.TryGetData<Color>(uid, PersonalLoadoutVisuals.Color, out var color))
            return;
        foreach (var key in args.RevealedLayers)
            _sprites.LayerSetColor((args.Equipee, sprite), key, color);
    }
}

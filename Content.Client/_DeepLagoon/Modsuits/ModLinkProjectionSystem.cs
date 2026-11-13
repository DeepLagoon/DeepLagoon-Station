using Content.Client._Goobstation.Clothing.Components;
using Content.Client.Clothing;
using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared.Inventory;
using Robust.Client.GameObjects;
using Robust.Client.ResourceManagement;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.TypeSerializers.Implementations;

namespace Content.Client._DeepLagoon.Modsuits;

[RegisterComponent]
public sealed partial class ModLinkAvatarVisualsComponent : Component;

public sealed partial class ModLinkProjectionSystem : EntitySystem
{
    [Dependency] private ClientClothingSystem _clothing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IResourceCache _resources = default!;
    [Dependency] private ISerializationManager _serialization = default!;
    [Dependency] private SpriteSystem _sprites = default!;

    private readonly HashSet<EntityUid> _changed = new();

    public override void Initialize()
    {
        UpdatesOutsidePrediction = true;
        SubscribeLocalEvent<ModLinkProjectedClothingComponent, AfterAutoHandleStateEvent>(OnClothingState);
        SubscribeLocalEvent<ModLinkAvatarVisualsComponent, ComponentStartup>(OnAvatarStartup);
    }

    private void OnAvatarStartup(Entity<ModLinkAvatarVisualsComponent> avatar, ref ComponentStartup args)
    {
        if (!TryComp<SpriteComponent>(avatar, out var sprite))
            return;

        sprite.EnableDirectionOverride = true;
        sprite.DirectionOverride = Direction.South;
    }

    private void OnClothingState(Entity<ModLinkProjectedClothingComponent> garment, ref AfterAutoHandleStateEvent args)
    {
        if (garment.Comp.Appearance is not { } appearance || !_prototypes.TryIndex(appearance.Prototype, out var prototype))
            return;

        // Default clothing states resolve against the copied item's RSI if Clothing.sprite was omitted.
        if (TryComp<SpriteComponent>(garment, out var sprite))
        {
            if (prototype.TryComp(out SpriteComponent? original, Factory) && original.BaseRSI is { } originalRsi)
                _sprites.SetBaseRsi((garment.Owner, sprite), originalRsi);
            else if (appearance.ItemRsi is { } path &&
                     _resources.TryGetResource<RSIResource>(SpriteSpecifierSerializer.TextureRoot / path, out var resource))
                _sprites.SetBaseRsi((garment.Owner, sprite), resource.RSI);
        }

        if (prototype.TryComp(out SealableClothingVisualsComponent? sealedVisuals, Factory))
        {
            var visualizer = EnsureComp<SealableClothingVisualsComponent>(garment);
            _serialization.CopyTo(sealedVisuals, ref visualizer, notNullableOverride: true);
            UseSpeciesLayers(garment.Comp, visualizer.ClothingVisuals);
        }

        var visual = _serialization.CreateCopy(appearance, notNullableOverride: true);
        UseSpeciesLayers(garment.Comp, visual.ClothingVisuals);
        if (visual.PaletteColor is { } color && garment.Comp.Slot is { } slot &&
            visual.ClothingVisuals.TryGetValue(slot, out var layers))
        {
            foreach (var layer in layers)
                layer.Color = color;
        }

        _clothing.CopyVisuals(garment, visual);
        _changed.Add(garment);
    }

    private static void UseSpeciesLayers(ModLinkProjectedClothingComponent garment, Dictionary<string, List<PrototypeLayerData>> styles)
    {
        if (garment.Slot is { } slot && garment.Species is { } species &&
            styles.TryGetValue($"{slot}-{species}", out var layers))
            styles[slot] = layers;
    }

    public override void Update(float frameTime)
    {
        if (_changed.Count == 0)
            return;

        var wearers = new HashSet<EntityUid>();
        foreach (var item in _changed)
        {
            if (TryComp<TransformComponent>(item, out var transform))
                wearers.Add(transform.ParentUid);
        }

        _changed.Clear();
        foreach (var wearer in wearers)
        {
            if (TryComp<InventoryComponent>(wearer, out var inventory))
                _clothing.InitClothing(wearer, inventory);
        }
    }
}

using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client.Humanoid;

public sealed class CharacterPreviewSpriteView : SpriteView
{
    private readonly IPrototypeManager _prototypes = IoCManager.Resolve<IPrototypeManager>();
    private readonly SortedSet<int> _wings = new();

    protected override void Draw(IRenderHandle handle)
    {
        if (OverrideDirection != Direction.North || Entity is not { } preview ||
            !EntMan.TryGetComponent<HumanoidAppearanceComponent>(preview.Owner, out var appearance))
        {
            base.Draw(handle);
            return;
        }

        var sprite = (preview.Owner, preview.Comp1);
        var sprites = SpriteSystem ??= EntMan.System<SpriteSystem>();
        if (sprites.LayerMapTryGet(sprite, HumanoidVisualLayers.Wings, out var layerIndex, logMissing: false))
            AddWing(preview.Comp1, layerIndex);

        foreach (var markings in appearance.MarkingSet.Markings.Values)
        foreach (var marking in markings)
        {
            if (!_prototypes.TryIndex<MarkingPrototype>(marking.MarkingId, out var prototype) ||
                (prototype.MarkingCategory != MarkingCategories.Wings && prototype.FilterType != MarkingEditorType.Wings))
                continue;

            foreach (var specifier in prototype.Sprites)
            {
                if (specifier is SpriteSpecifier.Rsi rsi &&
                    sprites.LayerMapTryGet(sprite, $"{prototype.ID}-{rsi.RsiState}", out layerIndex, logMissing: false))
                    AddWing(preview.Comp1, layerIndex);
            }
        }

        // Keep the original layer keys and order available to any other view of
        // the sprite. Render temporary copies in their original relative order.
        var firstCopy = preview.Comp1.LayerData.Count;
        try
        {
            foreach (var index in _wings)
            {
                var layer = preview.Comp1.LayerData[index];
                sprites.AddLayer(sprite, new SpriteComponent.Layer(layer, preview.Comp1));
                sprites.LayerSetVisible(layer, false);
            }
            base.Draw(handle);
        }
        finally
        {
            while (preview.Comp1.LayerData.Count > firstCopy)
                sprites.RemoveLayer(sprite, preview.Comp1.LayerData.Count - 1);
            foreach (var index in _wings)
                sprites.LayerSetVisible(preview.Comp1.LayerData[index], true);
            _wings.Clear();
        }
    }

    private void AddWing(SpriteComponent sprite, int index)
    {
        if (sprite.LayerData[index] is { Visible: true, Blank: false })
            _wings.Add(index);
    }
}

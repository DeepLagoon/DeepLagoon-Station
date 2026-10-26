using System.Linq;
using Content.Client._DeepLagoon.Tattoos;
using Content.Shared.Humanoid;
using Robust.Client.GameObjects;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private string _hairPreviewKey = "";
    private string _hairPreviewImage = "";

    private string HairPreviewImage()
    {
        if (!_entManager.TryGetComponent<SpriteComponent>(PreviewDummy, out var sprite)
            || !_entManager.TryGetComponent<HumanoidAppearanceComponent>(PreviewDummy, out var humanoid)
            || !TattooBodyPreview.TrySource(humanoid, sprite, _prototypeManager, HumanoidVisualLayers.Head, out var anchor))
            return "";

        var parts = new[] { HumanoidVisualLayers.Head, HumanoidVisualLayers.Eyes, HumanoidVisualLayers.Snout,
            HumanoidVisualLayers.HeadTop, HumanoidVisualLayers.HeadSide };
        var indices = parts.SelectMany(part => TattooBodyPreview.MarkingLayers(humanoid, sprite, _prototypeManager, part))
            .Concat(parts.Where(part => sprite.LayerMapTryGet(part, out _)).Select(part => sprite.LayerMapGet(part)))
            .Distinct().Where(index => sprite[index].Visible).Order().ToArray();
        var key = TattooBodyPreview.Key(sprite, indices, 0, animation: false);
        if (key == _hairPreviewKey) return _hairPreviewImage;
        using var image = TattooBodyPreview.RenderLayers(sprite, anchor, indices, 0);
        _hairPreviewImage = TattooSpriteFrames.Png(image);
        _hairPreviewKey = key;
        return _hairPreviewImage;
    }
}

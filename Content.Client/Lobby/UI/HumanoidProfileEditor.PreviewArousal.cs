using System.Linq;
using Content.Client._DeepLagoon.BodyCustomization;
using Content.Shared.Humanoid;
using Robust.Client.GameObjects;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private bool _previewGenitalAroused;
    private bool CanPreviewArousal => Profile?.Appearance.BodyFeatures.Any(feature => feature.Part == "penis" && feature.Enabled) == true;

    private void ApplyPreviewArousal()
    {
        if (!_entManager.TryGetComponent<HumanoidAppearanceComponent>(PreviewDummy, out var humanoid)
            || !_entManager.TryGetComponent<SpriteComponent>(PreviewDummy, out var sprite)) return;
        if (!CanPreviewArousal) _previewGenitalAroused = false;
        humanoid.GenitalAroused = _previewGenitalAroused;
        _entManager.System<BodyCustomizationVisualizerSystem>().Refresh(PreviewDummy, humanoid, sprite);
    }
}

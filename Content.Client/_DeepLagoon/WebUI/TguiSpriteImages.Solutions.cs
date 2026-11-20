using System.Globalization;
using System.Linq;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Rounding;
using Robust.Client.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._DeepLagoon.WebUI;

public sealed partial class TguiSpriteImages
{
    // Prototype sprites have not received appearance updates, so their liquid layers
    // still look empty. Reconstruct the initial fill without spawning preview entities.
    private IEnumerable<TguiData> ItemSpriteImages(EntityPrototype prototype, SpriteComponent sprite)
    {
        if (!prototype.TryGetComponent<SolutionContainerVisualsComponent>(out var visuals) ||
            visuals.Metamorphic || visuals.FillBaseName == null || visuals.MaxFillLevels <= 0 ||
            !prototype.TryGetComponent<SolutionContainerManagerComponent>(out var manager) ||
            manager.Solutions is not { } solutions ||
            !sprite.LayerMapTryGet(visuals.Layer, out var fillLayer))
            return SpriteImages(sprite);

        var solution = !string.IsNullOrEmpty(visuals.SolutionName)
            ? solutions.GetValueOrDefault(visuals.SolutionName)
            : solutions.Count == 1 ? solutions.Values.First() : null;
        if (solution == null || solution.Volume <= 0)
            return SpriteImages(sprite);

        var level = ContentHelpers.RoundToLevels(Math.Min(solution.FillFraction, 1f), 1, visuals.MaxFillLevels + 1);
        var state = visuals.FillBaseName + level.ToString(CultureInfo.InvariantCulture);
        var layer = sprite.LayerData[fillLayer];
        SpriteSpecifier? fillSprite = visuals.MetamorphicDefaultSprite;
        if (fillSprite is SpriteSpecifier.Rsi overrideRsi)
            fillSprite = new SpriteSpecifier.Rsi(overrideRsi.RsiPath, state);
        else if (fillSprite == null && layer.ActualRsi is { } rsi && rsi.TryGetState(state, out _))
            fillSprite = new SpriteSpecifier.Rsi(rsi.Path, state);
        if (fillSprite == null)
            return SpriteImages(sprite);

        var color = visuals.ChangeColor ? solution.GetColor(IoCManager.Resolve<IPrototypeManager>()) : Color.White;
        var result = new List<TguiData>();
        for (var index = 0; index < sprite.LayerData.Count; index++)
        {
            ISpriteLayer current = sprite.LayerData[index];
            if (index == fillLayer)
                result.Add(new TguiData().String("url", Frame(fillSprite)).String("color", (color * sprite.Color).ToHexNoAlpha()));
            else if (current.Visible && current.ActualRsi != null && current.RsiState.IsValid)
                result.Add(new TguiData().String("url", Frame(new SpriteSpecifier.Rsi(current.ActualRsi.Path, current.RsiState.Name!)))
                    .String("color", (current.Color * sprite.Color).ToHexNoAlpha()));
        }
        return result;
    }
}

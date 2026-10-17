using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Wagging;
using Robust.Client.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client.Wagging;

/// <summary>Control playback of authored tail wagging animations.</summary>
public sealed class WaggingSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly SpriteSystem _sprites = default!;

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);
        var query = EntityQueryEnumerator<WaggingComponent, HumanoidAppearanceComponent, SpriteComponent>();
        while (query.MoveNext(out var uid, out var wag, out var humanoid, out var sprite))
        {
            if (!humanoid.MarkingSet.Markings.TryGetValue(MarkingCategories.Tail, out var markings))
                continue;
            foreach (var marking in markings)
            {
                if (!WaggingMarkings.TryGetPair(_prototypes, marking.MarkingId, wag.Suffix, out _, out var animated)
                    || marking.MarkingId != animated
                    || !_prototypes.TryIndex<MarkingPrototype>(animated, out var proto))
                    continue;
                foreach (var specifier in proto.Sprites)
                {
                    if (specifier is not SpriteSpecifier.Rsi rsi || !sprite.LayerMapTryGet($"{marking.MarkingId}-{rsi.RsiState}", out var layer))
                        continue;
                    _sprites.LayerSetAutoAnimated((uid, sprite), layer, wag.Wagging);
                    if (!wag.Wagging)
                        _sprites.LayerSetAnimationTime((uid, sprite), layer, 0);
                }
            }
        }
    }
}

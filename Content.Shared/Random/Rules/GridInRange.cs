using System.Numerics;
using Robust.Shared.Map;

namespace Content.Shared.Random.Rules;

/// <summary>
/// Returns true if on a grid or in range of one.
/// </summary>
public sealed partial class GridInRangeRule : RulesRule
{
    [DataField]
    public float Range = 10f;

    public override bool Check(EntityManager entManager, EntityUid uid)
    {
        if (!entManager.TryGetComponent(uid, out TransformComponent? xform))
        {
            return false;
        }

        if (xform.GridUid != null)
        {
            return !Inverted;
        }

        var transform = entManager.System<SharedTransformSystem>();
        var mapManager = entManager.System<SharedMapSystem>();

        var worldPos = transform.GetWorldPosition(xform);
        var gridRange = new Vector2(Range, Range);

        var found = false;
        mapManager.FindGridsIntersecting(xform.MapID, new Box2(worldPos - gridRange, worldPos + gridRange),
            (_, _) =>
            {
                found = true;
                return false;
            }, includeMap: false);
        return found != Inverted;
    }
}

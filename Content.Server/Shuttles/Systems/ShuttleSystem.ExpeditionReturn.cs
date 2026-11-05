using System.Numerics;
using Content.Server._DeepLagoon.SpaceEnvironment;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Utility;

namespace Content.Server.Shuttles.Systems;

public sealed partial class ShuttleSystem
{
    private static readonly float[] ExpeditionReturnSearchRadii = [75f, 150f, 300f, 600f, 1200f, 2000f];

    private bool TryFindExpeditionReturn(
        EntityUid shuttle,
        EntityCoordinates target,
        Angle angle,
        HashSet<EntityUid> docked,
        out EntityCoordinates result)
    {
        var shuttleRotation = _transform.GetWorldRotation(shuttle);
        var shuttlePosition = _transform.GetWorldPosition(shuttle);
        var rotation = Matrix3x2.CreateRotation((float) angle.Theta);
        var bounds = rotation.TransformBox(Comp<MapGridComponent>(shuttle).LocalAABB);

        foreach (var linked in docked)
        {
            if (linked == shuttle || !TryComp<MapGridComponent>(linked, out var grid))
                continue;

            var offset = (angle - shuttleRotation).RotateVec(_transform.GetWorldPosition(linked) - shuttlePosition);
            var linkedAngle = angle + _transform.GetWorldRotation(linked) - shuttleRotation;
            var linkedTransform = Matrix3x2.CreateRotation((float) linkedAngle.Theta) * Matrix3x2.CreateTranslation(offset);
            bounds = bounds.Union(linkedTransform.TransformBox(grid.LocalAABB));
        }

        bounds = bounds.Enlarged(8f);
        if (TrySearchExpeditionReturn(target, bounds, out result))
            return true;

        return _mapSystem.TryGetMap(_ticker.DefaultMap, out var fallback) &&
               TrySearchExpeditionReturn(new EntityCoordinates(fallback.Value, new Vector2(1500f, 0f)), bounds, out result);
    }

    private bool TrySearchExpeditionReturn(EntityCoordinates target, Box2 bounds, out EntityCoordinates result)
    {
        result = EntityCoordinates.Invalid;
        if (TerminatingOrDeleted(target.EntityId) || !TryComp<MapComponent>(target.EntityId, out var map))
            return false;

        var space = EntityManager.System<SpaceEnvironmentSystem>();
        var grids = new List<Entity<MapGridComponent>>();
        bool IsFree(Vector2 position)
        {
            if (space.BlocksFtl(target.EntityId, position))
                return false;

            grids.Clear();
            _mapManager.FindGridsIntersecting(map.MapId, bounds.Translated(position), ref grids, includeMap: false);
            return grids.Count == 0;
        }

        if (IsFree(target.Position))
        {
            result = target;
            return true;
        }

        foreach (var radius in ExpeditionReturnSearchRadii)
        {
            for (var i = 0; i < 8; i++)
            {
                var position = target.Position + new Angle(i * Math.PI / 4).ToVec() * radius;
                if (!IsFree(position))
                    continue;

                result = target.WithPosition(position);
                return true;
            }
        }

        return false;
    }
}

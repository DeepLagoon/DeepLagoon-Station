using Content.Client.IconSmoothing;
using Content.Client._CE.IconSmoothing;
using Content.Shared._DeepLagoon.Mapping;
using Robust.Shared.Map.Components;

namespace Content.Client._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{
    private void RepairConnections(GridEditorRepairConnectionsEvent args) => RefreshGridConnections(GetEntity(args.Grid));

    public int RefreshGridConnections(EntityUid grid)
    {
        if (!HasComp<MapGridComponent>(grid)) return 0;
        var smooth = EntityManager.System<IconSmoothSystem>();
        var ceSmooth = EntityManager.System<CEIconSmoothSystem>();
        var refreshed = new HashSet<EntityUid>();
        var query = EntityQueryEnumerator<IconSmoothComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var component, out var transform))
        {
            if (transform.GridUid != grid) continue;
            smooth.DirtyNeighbours(uid, component, transform);
            refreshed.Add(uid);
        }
        var ceQuery = EntityQueryEnumerator<CEIconSmoothComponent, TransformComponent>();
        while (ceQuery.MoveNext(out var uid, out var component, out var transform))
        {
            if (transform.GridUid != grid) continue;
            ceSmooth.DirtyNeighbours(uid, component, transform);
            refreshed.Add(uid);
        }
        return refreshed.Count;
    }
}

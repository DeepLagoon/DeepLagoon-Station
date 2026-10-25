using Content.Shared.Decals;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Map;

namespace Content.Client.Decals.Overlays;

public sealed class PausedDecalOverlay(IEntityManager entities, DecalOverlay decals) : Overlay
{
    public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowEntities;

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (args.MapId == MapId.Nullspace)
            return;

        // Frozen mapping grids are not guaranteed to have initialized physics fixtures.
        var query = entities.AllEntityQueryEnumerator<DecalGridComponent, TransformComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out _, out var transform, out var metadata))
        {
            if (metadata.EntityPaused && transform.MapID == args.MapId)
                decals.DrawGrid(args, uid);
        }
    }
}

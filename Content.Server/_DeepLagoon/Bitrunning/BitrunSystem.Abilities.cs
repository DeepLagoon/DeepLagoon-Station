using System.Numerics;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.Physics;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.Server._DeepLagoon.Bitrunning;

public sealed partial class BitrunSystem
{
    private void InitializeAbilities()
    {
        SubscribeLocalEvent<BitrunForceWallEvent>(OnForceWall);
    }

    private void OnForceWall(BitrunForceWallEvent args)
    {
        if (args.Handled || !HasComp<BitrunAvatarComponent>(args.Performer) ||
            Transform(args.Performer).MapUid is not { } map || !_domains.ContainsKey(map) ||
            Transform(args.Performer).GridUid is not { } grid || !TryComp<MapGridComponent>(grid, out var gridComp) ||
            !_turf.TryGetTileRef(Transform(args.Performer).Coordinates, out var tile))
            return;
        var direction = Transform(args.Performer).LocalRotation.GetCardinalDir();
        var across = direction is Direction.North or Direction.South ? new Vector2i(1, 0) : new Vector2i(0, 1);
        foreach (var offset in new[] { -across, Vector2i.Zero, across })
        {
            var coords = _maps.GridTileToLocal(grid, gridComp, tile.Value.GridIndices + offset);
            var wall = Spawn("BitrunForceWall", coords);
            var pass = EnsureComp<PreventCollideComponent>(wall);
            pass.Uid = args.Performer;
            Dirty(wall, pass);
        }
        args.Handled = true;
    }
}

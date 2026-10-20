using System.Numerics;
using Content.Client.Pinpointer.UI;
using Content.Shared.Shuttles.BUIStates;
using Robust.Client.Graphics;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Shuttles;

/// <summary>The crew monitor's navigation map with the ship's retained damage overlay.</summary>
public sealed class ShuttleHullNavMapControl : NavMapControl
{
    public ShuttleHullState? Hull;
    private EntityUid? _framedGrid;

    public ShuttleHullNavMapControl()
    {
        WallColor = new Color(134, 147, 149);
        TileColor = new Color(67, 82, 87);
        BackgroundColor = Color.FromSrgb(new Color(16, 23, 27));
        PostWallDrawingAction += DrawHull;
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (MapUid == null || _framedGrid == MapUid || Hull is not { Width: > 0 } hull
            || !EntManager.TryGetComponent(MapUid, out MapGridComponent? grid))
            return;
        // Fit the retained design, including holes, once per ship. Subsequent updates preserve pan/zoom.
        _framedGrid = MapUid;
        var extent = new Vector2(hull.Width, hull.Height) * hull.CellSize * grid.TileSize;
        var center = new Vector2(hull.OriginX, hull.OriginY) * grid.TileSize + extent / 2;
        Offset = Vector2.Zero;
        Offset = TargetOffset = center - GetOffset();
        WorldMaxRange = Math.Max(MaxDisplayedRange, Math.Max(extent.X, extent.Y));
        WorldRange = Math.Clamp(Math.Max(extent.X, extent.Y) * 0.6f, MinDisplayedRange, WorldMaxRange);
    }

    private void DrawHull(DrawingHandleScreen handle)
    {
        if (Hull is not { Width: > 0 } hull || !EntManager.TryGetComponent(MapUid, out MapGridComponent? grid))
            return;
        var offset = GetOffset();
        for (var i = 0; i < hull.Cells.Length; i++)
        {
            var flags = hull.Cells[i];
            // Keep the real walls and doors visible; highlight losses and installed equipment.
            if ((flags & (2 | 4 | 8)) == 0)
                continue;
            var color = (flags & 2) != 0 ? new Color(204, 68, 66, 210)
                : (flags & 8) != 0 ? new Color(189, 185, 144, 180) : new Color(112, 157, 167, 180);
            var x = (hull.OriginX + i % hull.Width * hull.CellSize) * grid.TileSize - offset.X;
            var y = (hull.OriginY + i / hull.Width * hull.CellSize) * grid.TileSize - offset.Y;
            var size = hull.CellSize * grid.TileSize;
            handle.DrawRect(new UIBox2(ScalePosition(new Vector2(x, -y - size)),
                ScalePosition(new Vector2(x + size, -y))), color);
        }
    }
}

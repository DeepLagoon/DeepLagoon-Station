using Robust.Client.GameObjects;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Shared.Enums;

namespace Content.Client._DeepLagoon.Mapping;

/// <summary>
/// Highlights the tiles of the line or area that is being dragged with the mapper RCD.
/// </summary>
public sealed partial class MapperRcdShapeOverlay : Overlay
{
    private static readonly Color Fill = Color.Gold.WithAlpha(0.3f);

    [Dependency] private IEntityManager _entManager = default!;

    private readonly MapperRcdBrushSystem _brush;
    private readonly TransformSystem _transform;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    public MapperRcdShapeOverlay(MapperRcdBrushSystem brush)
    {
        IoCManager.InjectDependencies(this);

        _brush = brush;
        _transform = _entManager.System<TransformSystem>();
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!_brush.TryGetShape(out var grid, out var cells) || _transform.GetMapId(grid) != args.MapId)
            return;

        var handle = args.WorldHandle;
        handle.SetTransform(_transform.GetRenderWorldMatrix(grid));

        foreach (var cell in cells)
        {
            handle.DrawRect(Box2.FromDimensions(new Vector2(cell.X, cell.Y), Vector2.One), Fill);
        }

        handle.SetTransform(Matrix3x2.Identity);
    }
}

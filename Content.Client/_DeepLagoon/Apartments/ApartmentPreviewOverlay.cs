using System.Numerics;
using Content.Shared._DeepLagoon.Apartments;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.Apartments;

/// <summary>Draw prototype textures directly: no ghost entities, physics bodies or server traffic.</summary>
public sealed class ApartmentPreviewOverlay : Overlay
{
    private readonly ApartmentEditorSystem _editor;
    private readonly IEntityManager _entities;
    private readonly TransformSystem _transform;
    private readonly SpriteSystem _sprites;
    private readonly IPrototypeManager _prototypes;
    public override OverlaySpace Space => OverlaySpace.WorldSpaceEntities;

    public ApartmentPreviewOverlay(ApartmentEditorSystem editor, IEntityManager entities)
    {
        _editor = editor; _entities = entities;
        _transform = entities.System<TransformSystem>();
        _sprites = entities.System<SpriteSystem>();
        _prototypes = IoCManager.Resolve<IPrototypeManager>();
        ZIndex = 1000;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!_editor.IsOpen || !_editor.ShowDraft || _editor.Grid is not { } grid || _editor.Template == null ||
            !_entities.TryGetComponent<TransformComponent>(grid, out var transform) || transform.MapID != args.MapId) return;
        ApartmentFurniturePrototype? Catalog(string id) => _prototypes.TryIndex<ApartmentFurniturePrototype>(id, out var item) ? item : null;
        var valid = ApartmentLayout.Validate(_editor.Template, _editor.Draft, Catalog) == null;
        var handle = args.WorldHandle;
        handle.SetTransform(_transform.GetRenderWorldMatrix(grid));
        foreach (var p in _editor.Draft)
        {
            if (Catalog(p.Furniture) is not { } definition) continue;
            var texture = _sprites.Frame0(_prototypes.Index(definition.Entity));
            var center = new Vector2(p.X + .5f, p.Y + .5f);
            var size = new Vector2(texture.Width, texture.Height) / 32;
            var box = new Box2Rotated(Box2.CenteredAround(center, size), Angle.FromDegrees(p.Rotation * 90), center);
            handle.DrawTextureRect(texture, box, new Color(1f, 1f, 1f, .65f));
            var outline = valid ? Color.Lime : Color.Red;
            foreach (var cell in ApartmentLayout.Cells(p, definition))
            {
                var low = new Vector2(cell.X, cell.Y);
                var high = low + Vector2.One;
                handle.DrawLine(low, new Vector2(high.X, low.Y), outline);
                handle.DrawLine(new Vector2(high.X, low.Y), high, outline);
                handle.DrawLine(high, new Vector2(low.X, high.Y), outline);
                handle.DrawLine(new Vector2(low.X, high.Y), low, outline);
            }
        }
        handle.SetTransform(Matrix3x2.Identity);
    }
}

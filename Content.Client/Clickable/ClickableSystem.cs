using System.Numerics;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Utility;
using Robust.Shared.Graphics;

namespace Content.Client.Clickable;

/// <summary>
/// Handles click detection for sprites.
/// </summary>
public sealed partial class ClickableSystem : EntitySystem
{
    [Dependency] private IClickMapManager _clickMapManager = default!;
    [Dependency] private TransformSystem _transforms = default!;
    [Dependency] private SpriteSystem _sprites = default!;

    private EntityQuery<ClickableComponent> _clickableQuery;
    private EntityQuery<TransformComponent> _xformQuery;

    public override void Initialize()
    {
        base.Initialize();
        _clickableQuery = GetEntityQuery<ClickableComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();
    }

    /// <summary>
    /// Used to check whether a click worked. Will first check if the click falls inside of some explicit bounding
    /// boxes (see <see cref="Bounds"/>). If that fails, attempts to use automatically generated click maps.
    /// </summary>
    /// <param name="worldPos">The world position that was clicked.</param>
    /// <param name="drawDepth">
    /// The draw depth for the sprite that captured the click.
    /// </param>
    /// <returns>Whether the cursor hit the sprite directly, within the pixel tolerance, or missed.</returns>
    public ClickHit CheckClick(Entity<ClickableComponent?, SpriteComponent, TransformComponent?> entity, Vector2 worldPos, IEye eye, out int drawDepth, out uint renderOrder, out float bottom)
    {
        if (!_clickableQuery.Resolve(entity.Owner, ref entity.Comp1, false))
        {
            drawDepth = default;
            renderOrder = default;
            bottom = default;
            return ClickHit.None;
        }

        if (!_xformQuery.Resolve(entity.Owner, ref entity.Comp3))
        {
            drawDepth = default;
            renderOrder = default;
            bottom = default;
            return ClickHit.None;
        }

        var sprite = entity.Comp2;
        var transform = entity.Comp3;

        if (!sprite.Visible)
        {
            drawDepth = default;
            renderOrder = default;
            bottom = default;
            return ClickHit.None;
        }

        drawDepth = sprite.DrawDepth;
        renderOrder = sprite.RenderOrder;
        var (spritePos, spriteRot) = _transforms.GetRenderWorldPositionRotation((transform.Owner, transform));
        var spriteBB = sprite.CalculateRotatedBoundingBox(spritePos, spriteRot, eye.Rotation);
        bottom = Matrix3Helpers.CreateRotation(eye.Rotation).TransformBox(spriteBB).Bottom;

        Matrix3x2.Invert(sprite.GetLocalMatrix(), out var invSpriteMatrix);

        // This should have been the rotation of the sprite relative to the screen, but this is not the case with no-rot or directional sprites.
        var relativeRotation = (spriteRot + eye.Rotation).Reduced().FlipPositive();

        var cardinalSnapping = sprite.SnapCardinals ? relativeRotation.GetCardinalDir().ToAngle() : Angle.Zero;

        // First we get `localPos`, the clicked location in the sprite-coordinate frame.
        var entityXform = Matrix3Helpers.CreateInverseTransform(spritePos, sprite.NoRotation ? -eye.Rotation : spriteRot - cardinalSnapping);
        var localPos = Vector2.Transform(Vector2.Transform(worldPos, entityXform), invSpriteMatrix);

        // Check explicitly defined click-able bounds
        if (CheckDirBound((entity.Owner, entity.Comp1, entity.Comp2), relativeRotation, localPos))
            return ClickHit.Direct;

        // Next check each individual sprite layer using automatically computed click maps.
        var bestHit = ClickHit.None;
        foreach (var spriteLayer in sprite.AllLayers)
        {
            if (spriteLayer is not SpriteComponent.Layer layer || !_sprites.IsVisible(layer))
            {
                continue;
            }

            // Check the layer's texture, if it has one
            if (layer.Texture != null)
            {
                // Convert to image coordinates
                var imagePos = ToImagePixel(localPos, layer.Texture.Size);
                var hit = _clickMapManager.GetClickHit(layer.Texture, imagePos);

                if (hit == ClickHit.Direct)
                    return hit;
                if (hit == ClickHit.Nearby)
                    bestHit = hit;
            }

            // Either we weren't clicking on the texture, or there wasn't one. In which case: check the RSI next
            if (layer.ActualRsi is not { } rsi || !rsi.TryGetState(layer.State, out var rsiState))
                continue;

            var dir = SpriteComponent.Layer.GetDirection(rsiState.RsiDirections, relativeRotation);

            // convert to layer-local coordinates
            layer.GetLayerDrawMatrix(dir, out var matrix);
            Matrix3x2.Invert(matrix, out var inverseMatrix);
            var layerLocal = Vector2.Transform(localPos, inverseMatrix);

            // Convert to image coordinates
            var layerImagePos = ToImagePixel(layerLocal, rsiState.Size);

            // Next, to get the right click map we need the "direction" of this layer that is actually being used to draw the sprite on the screen.
            // This **can** differ from the dir defined before, but can also just be the same.
            if (sprite.EnableDirectionOverride)
                dir = sprite.DirectionOverride.Convert(rsiState.RsiDirections);
            dir = dir.OffsetRsiDir(layer.DirOffset);

            var layerHit = _clickMapManager.GetClickHit(rsi, layer.State, dir, layer.AnimationFrame, layerImagePos);
            if (layerHit == ClickHit.Direct)
                return layerHit;
            if (layerHit == ClickHit.Nearby)
                bestHit = layerHit;
        }

        if (bestHit != ClickHit.None)
            return bestHit;

        drawDepth = default;
        renderOrder = default;
        bottom = default;
        return ClickHit.None;
    }

    private static Vector2i ToImagePixel(Vector2 localPos, Vector2i size)
    {
        var pos = localPos * EyeManager.PixelsPerMeter * new Vector2(1, -1) + size / 2f;
        return new Vector2i((int) MathF.Floor(pos.X), (int) MathF.Floor(pos.Y));
    }

    public bool CheckDirBound(Entity<ClickableComponent, SpriteComponent> entity, Angle relativeRotation, Vector2 localPos)
    {
        var clickable = entity.Comp1;
        var sprite = entity.Comp2;

        if (clickable.Bounds == null)
            return false;

        // These explicit bounds only work for either 1 or 4 directional sprites.

        // This would be the orientation of a 4-directional sprite.
        var direction = relativeRotation.GetCardinalDir();

        var modLocalPos = sprite.NoRotation
            ? localPos
            : direction.ToAngle().RotateVec(localPos);

        // First, check the bounding box that is valid for all orientations
        if (clickable.Bounds.All.Contains(modLocalPos))
            return true;

        // Next, get and check the appropriate bounding box for the current sprite orientation
        var boundsForDir = (sprite.EnableDirectionOverride ? sprite.DirectionOverride : direction) switch
        {
            Direction.East => clickable.Bounds.East,
            Direction.North => clickable.Bounds.North,
            Direction.South => clickable.Bounds.South,
            Direction.West => clickable.Bounds.West,
            _ => throw new InvalidOperationException()
        };

        return boundsForDir.Contains(modLocalPos);
    }
}

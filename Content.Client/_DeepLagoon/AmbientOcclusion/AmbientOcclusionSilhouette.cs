using System.Numerics;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Utility;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Maths;
using static Robust.Client.GameObjects.SpriteComponent;

namespace Content.Client._DeepLagoon.AmbientOcclusion;

/// <summary>Read-only silhouette rendering through the public sprite/layer API.</summary>
internal static class AmbientOcclusionSilhouette
{
    public static void Render(SpriteSystem sprites, SpriteComponent sprite, DrawingHandleWorld drawingHandle,
        Angle eyeRotation, Angle worldRotation, Vector2 worldPosition, Direction? overrideDirection,
        Color tint, float silhouetteScale = 1f)
    {
        var angle = worldRotation + eyeRotation; // angle on-screen. Used to decide the direction of 4/8 directional RSIs
        angle = angle.Reduced().FlipPositive();  // Reduce the angles to fix math shenanigans

        var cardinal = Angle.Zero;

        // If we have a 1-directional sprite then snap it to try and always face it south if applicable.
        if (sprite is {NoRotation: false, SnapCardinals: true})
            cardinal = angle.RoundToCardinalAngle();

        // worldRotation + eyeRotation should be the angle of the entity on-screen. If no-rot is enabled this is just set to zero.
        // However, at some point later the eye-matrix is applied separately, so we subtract -eye rotation for now:
        var entityMatrix = Matrix3Helpers.CreateTransform(worldPosition, sprite.NoRotation ? -eyeRotation : worldRotation - cardinal);
        var localMatrix = Matrix3x2.Multiply(Matrix3x2.CreateScale(silhouetteScale), sprite.LocalMatrix);
        var spriteMatrix = Matrix3x2.Multiply(localMatrix, entityMatrix);

        // Fast path for when all sprites use the same transform matrix
        if (!sprite.GranularLayersRendering)
        {
            foreach (var entry in sprite.AllLayers)
            {
                if (entry is not Layer layer) continue;
                RenderLayer(layer, drawingHandle, ref spriteMatrix, angle, overrideDirection, tint, sprite.Color.A, sprites);
            }
            return;
        }

        //Default rendering (NoRotation = false)
        entityMatrix = Matrix3Helpers.CreateTransform(worldPosition, worldRotation);
        var transformDefault = Matrix3x2.Multiply(localMatrix, entityMatrix);

        //Snap to cardinals
        entityMatrix = Matrix3Helpers.CreateTransform(worldPosition, worldRotation - angle.RoundToCardinalAngle());
        var transformSnap = Matrix3x2.Multiply(localMatrix, entityMatrix);

        //No rotation
        entityMatrix = Matrix3Helpers.CreateTransform(worldPosition, -eyeRotation);
        var transformNoRot = Matrix3x2.Multiply(localMatrix, entityMatrix);

        foreach (var entry in sprite.AllLayers)
        {
            if (entry is not Layer layer) continue;
            switch (layer.RenderingStrategy)
            {
                case LayerRenderingStrategy.UseSpriteStrategy:
                    RenderLayer(layer, drawingHandle, ref spriteMatrix, angle, overrideDirection, tint, sprite.Color.A, sprites);
                    break;
                case LayerRenderingStrategy.Default:
                    RenderLayer(layer, drawingHandle, ref transformDefault, angle, overrideDirection, tint, sprite.Color.A, sprites);
                    break;
                case LayerRenderingStrategy.NoRotation:
                    RenderLayer(layer, drawingHandle, ref transformNoRot, angle, overrideDirection, tint, sprite.Color.A, sprites);
                    break;
                case LayerRenderingStrategy.SnapToCardinals:
                    RenderLayer(layer, drawingHandle, ref transformSnap, angle, overrideDirection, tint, sprite.Color.A, sprites);
                    break;
                default:
                    break;
            }
        }
    }

    private static void RenderLayer(Layer layer, DrawingHandleWorld drawingHandle, ref Matrix3x2 spriteMatrix,
        Angle angle, Direction? overrideDirection, Color tint, float spriteAlpha, SpriteSystem sprites)
    {
        if (!layer.Visible || layer.Blank || layer.ShaderPrototype == SpriteSystem.UnshadedId || layer.Shader != null ||
            layer.CopyToShaderParameters != null || layer.Color.A <= 0)
            return;

        var state = layer.ActualState;
        var dir = state == null ? RsiDirection.South : Layer.GetDirection(state.RsiDirections, angle);
        layer.GetLayerDrawMatrix(dir, out var layerMatrix);
        if (overrideDirection != null && state != null)
            dir = overrideDirection.Value.Convert(state.RsiDirections);
        dir = dir.OffsetRsiDir(layer.DirOffset);
        var texture = state?.GetFrame(dir, layer.AnimationFrame) ?? layer.Texture ?? sprites.GetFallbackTexture();
        var transformMatrix = Matrix3x2.Multiply(layerMatrix, spriteMatrix);
        drawingHandle.SetTransform(in transformMatrix);
        var textureSize = texture.Size / (float) EyeManager.PixelsPerMeter;
        drawingHandle.DrawTextureRectRegion(texture, Box2.FromDimensions(textureSize / -2, textureSize),
            tint.WithAlpha(tint.A * spriteAlpha * layer.Color.A));
    }
}

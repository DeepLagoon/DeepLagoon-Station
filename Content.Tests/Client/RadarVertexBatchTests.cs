#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Client.Shuttles.UI;
using NUnit.Framework;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.Tests.Client;

[TestFixture]
public sealed class RadarVertexBatchTests
{
    [TestCase(DrawPrimitiveTopology.LineList, 2, 98_310)]
    [TestCase(DrawPrimitiveTopology.TriangleList, 3, 147_459)]
    public void LargeRadarMeshesSubmitCompletePrimitivesInBoundedBatches(
        DrawPrimitiveTopology topology, int verticesPerPrimitive, int count)
    {
        var mesh = new Vector2[count];
        for (var i = 0; i < mesh.Length; i++)
            mesh[i] = new Vector2(i, -i);

        using var handle = new CaptureHandle();
        BaseShuttleControl.DrawPrimitiveBatches(handle, topology, mesh, Color.White);
        Assert.That(handle.BatchSizes.Count, Is.GreaterThan(1));
        foreach (var size in handle.BatchSizes)
        {
            Assert.That(size, Is.InRange(verticesPerPrimitive, BaseShuttleControl.RadarVertexBatchSize));
            Assert.That(size % verticesPerPrimitive, Is.Zero);
        }

        Assert.That(handle.Submitted, Is.EqualTo(mesh));
    }

    private sealed class WhiteTexture() : Texture(Vector2i.One)
    {
        public override Color GetPixel(int x, int y) => Color.White;
    }

    private sealed class CaptureHandle() : DrawingHandleScreen(new WhiteTexture())
    {
        public readonly List<int> BatchSizes = new();
        public readonly List<Vector2> Submitted = new();

        public override void DrawPrimitives(DrawPrimitiveTopology topology, Texture texture, ReadOnlySpan<DrawVertexUV2DColor> vertices)
        {
            BatchSizes.Add(vertices.Length);
            foreach (var vertex in vertices)
                Submitted.Add(vertex.Position);
        }

        public override void DrawPrimitives(DrawPrimitiveTopology topology, Texture texture, ReadOnlySpan<ushort> indices,
            ReadOnlySpan<DrawVertexUV2DColor> vertices) => throw new NotSupportedException();
        public override void SetTransform(in Matrix3x2 matrix) => throw new NotSupportedException();
        public override Matrix3x2 GetTransform() => Matrix3x2.Identity;
        public override void UseShader(ShaderInstance? shader) => throw new NotSupportedException();
        public override ShaderInstance? GetShader() => null;
        public override void DrawCircle(Vector2 position, float radius, Color color, bool filled = true) => throw new NotSupportedException();
        public override void DrawLine(Vector2 from, Vector2 to, Color color) => throw new NotSupportedException();
        public override void RenderInRenderTarget(IRenderTarget target, Action action, Color? clearColor) => throw new NotSupportedException();
        public override void DrawTexture(Texture texture, Vector2 position, Color? modulate = null) => throw new NotSupportedException();
        public override void DrawRect(UIBox2 rect, Color color, bool filled = true) => throw new NotSupportedException();
        public override void DrawTextureRectRegion(Texture texture, UIBox2 rect, UIBox2? subRegion = null, Color? modulate = null)
            => throw new NotSupportedException();
        public override void DrawEntity(EntityUid entity, Vector2 position, Vector2 scale, Angle? worldRot,
            Angle eyeRotation = default, Direction? overrideDirection = null, SpriteComponent? sprite = null,
            TransformComponent? xform = null, SharedTransformSystem? xformSystem = null)
            => throw new NotSupportedException();
    }
}

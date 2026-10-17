using System;
using System.Numerics;
using Content.Client.Shuttles.UI;
using NUnit.Framework;
using Robust.Shared.Maths;

namespace Content.Tests.Client;

[TestFixture]
public sealed class ShuttleDockGeometryTests
{
    [Test]
    public void RotatedDockPreservesRectangle(
        [Values(0, 45, 90, 135, 180, 225, 270, 315)] int degrees,
        [Values(false, true)] bool connection,
        [Values(2f, 32f)] float scale)
    {
        var bounds = connection ? new Box2(-0.2f, -0.7f, 0.2f, -0.5f) : new Box2(-0.5f, -0.5f, 0.5f, 0.5f);
        var midpoint = new Vector2(137, 83);
        Span<Vector2> vertices = stackalloc Vector2[4];
        ShuttleDockControl.GetDockQuad(bounds, Angle.FromDegrees(degrees), scale, midpoint, vertices);

        var width = bounds.Width * scale;
        var height = bounds.Height * scale;
        var twiceArea = 0f;
        for (var i = 0; i < vertices.Length; i++)
        {
            var next = vertices[(i + 1) % 4];
            var edge = next - vertices[i];
            var nextEdge = vertices[(i + 2) % 4] - next;
            Assert.That(edge.Length(), Is.EqualTo(i % 2 == 0 ? width : height).Within(0.0001f));
            Assert.That(Vector2.Dot(edge, nextEdge), Is.EqualTo(0f).Within(0.002f));
            var relative = vertices[i] - midpoint;
            var nextRelative = next - midpoint;
            twiceArea += relative.X * nextRelative.Y - relative.Y * nextRelative.X;
        }

        Assert.That(MathF.Abs(twiceArea) / 2f, Is.EqualTo(width * height).Within(0.002f));
        var center = (vertices[0] + vertices[2]) / 2f;
        Assert.That(Vector2.Distance(center, midpoint),
            Is.EqualTo(connection ? 0.6f * scale : 0f).Within(0.0001f));
    }
}

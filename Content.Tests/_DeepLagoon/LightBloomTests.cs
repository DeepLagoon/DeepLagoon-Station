using System;
using System.Numerics;
using Content.Client._DeepLagoon.LightBloom;
using NUnit.Framework;

namespace Content.Tests._DeepLagoon;

[TestFixture]
public sealed class LightBloomTests
{
    private static readonly Vector2[] Wall = [new(-0.1f, -2), new(0.1f, -2), new(0.1f, 2), new(-0.1f, 2)];

    [TestCase(0f)]
    [TestCase(0.6f)]
    [TestCase(1.57f)]
    public void WallBlocksSourceAndHaloOnRotatingGrids(float rotation)
    {
        var geometry = new LightBloomGeometry();
        var matrix = Matrix3x2.CreateRotation(rotation) * Matrix3x2.CreateTranslation(14, -7);
        geometry.AddBlocker(Wall, matrix);
        var eye = Vector2.Transform(new Vector2(-1, 0), matrix);
        var source = Vector2.Transform(new Vector2(0.5f, 0), matrix);
        Assert.That(geometry.IsVisible(eye, source), Is.False);
        Assert.That(geometry.IsVisible(source, source + new Vector2(0.01f, 0)), Is.True);
        var vertices = new Vector2[LightBloomGeometry.MaxVertices];
        var count = geometry.Build(source, 1.5f, vertices);
        Assert.That(count, Is.GreaterThan(3));
        Matrix3x2.Invert(matrix, out var inverse);
        for (var i = 1; i < count; i++)
            Assert.That(Vector2.Transform(vertices[i], inverse).X, Is.GreaterThanOrEqualTo(0.0999f));
    }

    [Test]
    public void OpenDoorStopsBlockingImmediately()
    {
        var geometry = new LightBloomGeometry();
        geometry.AddBlocker(Wall, Matrix3x2.Identity);
        Assert.That(geometry.IsVisible(new(-1, 0), new(1, 0)), Is.False);
        geometry.Clear();
        Assert.That(geometry.IsVisible(new(-1, 0), new(1, 0)), Is.True);
    }

    [Test]
    public void ThinPolygonDoesNotLeakAndFanAnglesNeverOverlap()
    {
        var geometry = new LightBloomGeometry();
        geometry.AddBlocker([new(0.4f, -0.25f), new(0.401f, -0.25f), new(0.401f, 0.25f), new(0.4f, 0.25f)], Matrix3x2.Identity);
        var vertices = new Vector2[LightBloomGeometry.MaxVertices];
        var count = geometry.Build(Vector2.Zero, 1.5f, vertices);
        var totalAngle = 0f;
        for (var i = 1; i < count - 1; i++)
        {
            var a = vertices[i];
            var b = vertices[i + 1];
            var angle = MathF.Atan2(a.X * b.Y - a.Y * b.X, Vector2.Dot(a, b));
            Assert.That(angle, Is.GreaterThanOrEqualTo(-0.00001f));
            totalAngle += angle;
            for (var j = 1; j < 10; j++)
            {
                var point = Vector2.Lerp(a, b, j / 10f) * 0.99f;
                Assert.That(geometry.IsVisible(Vector2.Zero, point), Is.True, $"Leaking triangle {i}, sample {j}, a={a}, b={b}, point={point}");
            }
        }
        Assert.That(totalAngle, Is.EqualTo(MathF.Tau).Within(0.001f));
    }

    [Test]
    public void DenseOccludersOmitHaloRatherThanOmitWalls()
    {
        var geometry = new LightBloomGeometry();
        for (var i = 0; i < 80; i++)
            geometry.AddBlocker(Wall, Matrix3x2.CreateTranslation(i * 0.01f, 0));
        Assert.That(geometry.Build(new Vector2(1.2f, 0), 1.5f, new Vector2[LightBloomGeometry.MaxVertices]), Is.Zero);
    }
    [Test]
    public void EmbeddedSourceCannotProduceHaloEvenWithoutFov()
    {
        var geometry = new LightBloomGeometry();
        geometry.AddBlocker(Wall, Matrix3x2.Identity);
        Assert.That(geometry.IsVisible(new Vector2(-1, 0), Vector2.Zero), Is.False);
        Assert.That(geometry.Build(Vector2.Zero, 1.5f, new Vector2[LightBloomGeometry.MaxVertices]), Is.Zero);
    }

    [TestCase(0.2f)]
    [TestCase(0.8f)]
    [TestCase(1.5f)]
    public void PolygonCornersDoNotLeakBetweenMeshVertices(float radius)
    {
        var random = new Random(74219);
        var vertices = new Vector2[LightBloomGeometry.MaxVertices];
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var geometry = new LightBloomGeometry();
            var angle = (float)random.NextDouble() * MathF.Tau;
            var transform = Matrix3x2.CreateRotation(angle) * Matrix3x2.CreateTranslation(radius * 0.5f, 0);
            geometry.AddBlocker([new(-0.05f,-0.2f), new(0.05f,-0.2f), new(0.05f,0.2f), new(-0.05f,0.2f)], transform);
            var count = geometry.Build(Vector2.Zero, radius, vertices);
            for (var i = 1; i < count - 1; i++)
            for (var j = 1; j < 10; j++)
                Assert.That(geometry.IsVisible(Vector2.Zero, Vector2.Lerp(vertices[i], vertices[i + 1], j / 10f) * 0.98f), Is.True);
        }
    }
}
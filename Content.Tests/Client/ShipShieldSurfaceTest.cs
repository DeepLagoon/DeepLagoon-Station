using System;
using System.Collections;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using Content.Client._Crescent.ShipShields;
using Content.Shared._Crescent.ShipShields;
using Moq;
using NUnit.Framework;
using Robust.Client.Graphics;
using Robust.Shared.ContentPack;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Collision.Shapes;

namespace Content.Tests.Client;

[TestFixture]
public sealed class ShipShieldSurfaceTest
{
    [TestCase(300f, 300f, 50f, -700f, 900f)]
    [TestCase(600f, 80f, 50f, 100f, -400f)]
    [TestCase(80f, 600f, 50f, -300f, -200f)]
    [TestCase(300f, 240f, 0f, 0f, 0f)]
    public void LargeGridFitsInsideOpeningWithClearance(float width, float height, float padding, float x, float y)
    {
        var grid = Box2.CenteredAround(new Vector2(x, y), new Vector2(width, height));
        var occupied = grid.Enlarged(0.5f);
        var radii = ShipShieldGeometry.GetRadii(grid.Size, padding);
        var expansion = ShipShieldGeometry.GetEnclosingScale(occupied, grid.Center, radii);
        Assert.That(expansion, Is.GreaterThan(1f), "Reproduces the old fixed-padding intersection");
        radii *= expansion;

        var chain = new ChainShape();
        var radius = MathF.Min(radii.X, radii.Y);
        chain.CreateLoop(Vector2.Zero, radius);
        for (var i = 0; i < chain.Vertices.Length; i++)
            chain.Vertices[i] *= radii / radius;
        var mesh = new ShipShieldMesh(chain);
        var innerRadii = mesh.Bounds.Size * 0.5f *
            new Vector2(ShipShieldGeometry.InnerWidthRatio, ShipShieldGeometry.InnerHeightRatio);
        foreach (var corner in new[] { grid.BottomLeft, grid.BottomRight, grid.TopLeft, grid.TopRight })
        {
            Assert.That(((corner - grid.Center) / innerRadii).Length(), Is.LessThan(1f),
                "The actual replicated mesh and shader opening must leave the hull clear");
        }
    }

    [Test]
    public void SmallGridKeepsExistingShieldSize()
    {
        var grid = Box2.CenteredAround(Vector2.Zero, new Vector2(10, 8));
        var radii = ShipShieldGeometry.GetRadii(grid.Size, 50);
        Assert.That(radii, Is.EqualTo(new Vector2(30, 29)));
        Assert.That(ShipShieldGeometry.GetEnclosingScale(grid.Enlarged(0.5f), grid.Center, radii), Is.EqualTo(1f));
    }

    [Test]
    public void CrossShapedGridDoesNotFitNonexistentCornerTiles()
    {
        var radii = ShipShieldGeometry.GetRadii(new Vector2(300), 50);
        var horizontal = Box2.CenteredAround(Vector2.Zero, new Vector2(300, 20));
        var vertical = Box2.CenteredAround(Vector2.Zero, new Vector2(20, 300));
        var occupiedScale = MathF.Max(
            ShipShieldGeometry.GetEnclosingScale(horizontal, Vector2.Zero, radii),
            ShipShieldGeometry.GetEnclosingScale(vertical, Vector2.Zero, radii));
        var boxScale = ShipShieldGeometry.GetEnclosingScale(
            Box2.CenteredAround(Vector2.Zero, new Vector2(300)), Vector2.Zero, radii);
        Assert.That(occupiedScale, Is.LessThan(boxScale));
    }

    [TestCase(4f, 1f, 1f)]
    [TestCase(30f, 4f, 1f)]
    [TestCase(30f, 1f, 4f)]
    public void FanFillsEntireOvalAndIncludesClosingEdge(float radius, float scaleX, float scaleY)
    {
        var chain = new ChainShape();
        chain.CreateLoop(Vector2.Zero, radius);
        for (var i = 0; i < chain.Vertices.Length; i++)
            chain.Vertices[i] *= new Vector2(scaleX, scaleY);

        var mesh = new ShipShieldMesh(chain);
        Assert.That(mesh.Vertices[1].Position, Is.EqualTo(mesh.Vertices[^1].Position));
        Assert.That(mesh.Vertices[1].UV, Is.EqualTo(mesh.Vertices[^1].UV));

        double polygonArea = 0;
        double fanArea = 0;
        for (var i = 1; i < mesh.Vertices.Length - 1; i++)
        {
            var a = mesh.Vertices[i].Position;
            var b = mesh.Vertices[i + 1].Position;
            var triangleArea = Cross(a - mesh.Vertices[0].Position, b - mesh.Vertices[0].Position) * 0.5;
            Assert.That(triangleArea, Is.GreaterThan(0), "No overlapping or degenerate fan triangles");
            fanArea += triangleArea;
            polygonArea += Cross(a, b) * 0.5;
        }

        Assert.That(fanArea, Is.EqualTo(polygonArea).Within(0.001));
        Assert.That(fanArea, Is.EqualTo(Math.PI * radius * radius * scaleX * scaleY).Within(fanArea * 0.002));
        foreach (var vertex in mesh.Vertices)
        {
            Assert.That(vertex.UV.X, Is.InRange(0f, 1f));
            Assert.That(vertex.UV.Y, Is.InRange(0f, 1f));
        }
    }

    [Test]
    public void BoundsFollowRotatedShipFarFromMapOrigin()
    {
        var chain = new ChainShape();
        chain.CreateLoop(new[] { new Vector2(-40, -10), new Vector2(40, -10),
            new Vector2(40, 10), new Vector2(-40, 10) });
        var mesh = new ShipShieldMesh(chain);
        var matrix = Matrix3x2.CreateRotation(MathF.PI / 2) * Matrix3x2.CreateTranslation(500, 600);
        var worldBounds = matrix.TransformBox(mesh.Bounds);
        Assert.That(worldBounds.Intersects(Box2.CenteredAround(new Vector2(500, 600), new Vector2(5))), Is.True);
        Assert.That(worldBounds.Intersects(Box2.CenteredAround(Vector2.Zero, new Vector2(5))), Is.False);
        Assert.That(worldBounds.Width, Is.EqualTo(20f).Within(0.001));
        Assert.That(worldBounds.Height, Is.EqualTo(80f).Within(0.001));
    }

    [Test]
    public void SurfaceDoesNotReadUninitializedClydeColorOutput()
    {
        var path = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "../../Resources/Textures/_DeepLagoon/Shaders/ship_shield.swsl"));
        var parser = typeof(ShaderPrototype).Assembly.GetType("Robust.Client.Graphics.ShaderParser", true)!;
        var parse = parser.GetMethod("Parse", new[] { typeof(string), typeof(IResourceManager) })!;
        var parsed = parse.Invoke(null,
            new object[] { File.ReadAllText(path), new Mock<IResourceManager>().Object })!;
        var functions = (IEnumerable) parsed.GetType().GetProperty("Functions")!.GetValue(parsed)!;
        foreach (var function in functions)
        {
            var body = (string) function.GetType().GetProperty("Body")!.GetValue(function)!;
            // Clyde declares COLOR without initializing it and applies vertex modulation
            // afterwards. Reading it before assigning a fragment result makes alpha undefined.
            Assert.That(Regex.IsMatch(body, @"\bCOLOR\b(?!\s*=)"), Is.False,
                "Shader must not use the uninitialized COLOR output as an input");
        }
    }

    [Test]
    public void SurfaceShaderParsesAndGeneratesValidDecimalTokens()
    {
        var path = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "../../Resources/Textures/_DeepLagoon/Shaders/ship_shield.swsl"));
        var parser = typeof(ShaderPrototype).Assembly.GetType("Robust.Client.Graphics.ShaderParser", true)!;
        var parse = parser.GetMethod("Parse", new[] { typeof(string), typeof(IResourceManager) })!;
        var parsed = parse.Invoke(null,
            new object[] { File.ReadAllText(path), new Mock<IResourceManager>().Object })!;
        var functions = (IEnumerable) parsed.GetType().GetProperty("Functions")!.GetValue(parsed)!;
        foreach (var function in functions)
        {
            var body = (string) function.GetType().GetProperty("Body")!.GetValue(function)!;
            Assert.That(Regex.IsMatch(body, @"\.\s+\d"), Is.False, body);
        }
    }

    private static double Cross(Vector2 a, Vector2 b) => (double) a.X * b.Y - (double) a.Y * b.X;
}

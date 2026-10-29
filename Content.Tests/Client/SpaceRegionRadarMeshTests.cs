using System;
using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.SpaceEnvironment;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using NUnit.Framework;

namespace Content.Tests.Client;

[TestFixture]
public sealed class SpaceRegionRadarMeshTests
{
    [TestCase(0.3f, 1.7f)]
    [TestCase(1f, 0f)]
    public void FillCoversOnlyItsBoundaryWithoutGapsOrOverdraw(float aspect, float rotation)
    {
        foreach (var kind in Enum.GetValues<SpaceRegionKind>())
        {
            var region = new SpaceRegion { Id = "radar", Seed = 719, Kind = kind,
                X = 5200, Y = -1300, Radius = 700, Aspect = aspect, Rotation = rotation, Intensity = 0.8f };
            var contour = Enumerable.Range(0, SpaceRegionGeometry.ContourSegments)
                .Select(i => SpaceRegionGeometry.ContourPoint(region, i)).ToArray();
            var mesh = SpaceRegionRadarMesh.Build(region, contour);
            foreach (var vertex in mesh)
            {
                Assert.That(SpaceRegionGeometry.Local(region, vertex.Position).LengthSquared(), Is.LessThanOrEqualTo(1.00001f));
                Assert.That(vertex.Color.A, Is.InRange(0.01f, 0.16f), "The interior must stay visible and translucent");
            }
            float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
            var center = new Vector2(region.X, region.Y);
            var boundaryArea = 0f;
            for (var i = 0; i < contour.Length; i++)
                boundaryArea += Cross(contour[i] - center, contour[(i + 1) % contour.Length] - center) / 2;
            var fillArea = 0f;
            for (var i = 0; i < mesh.Length; i += 3)
                fillArea += MathF.Abs(Cross(mesh[i + 1].Position - mesh[i].Position, mesh[i + 2].Position - mesh[i].Position)) / 2;
            Assert.That(fillArea, Is.EqualTo(MathF.Abs(boundaryArea)).Within(MathF.Abs(boundaryArea) * 0.0001f));
            Assert.That(mesh.Select(v => v.Color.A).Distinct().Count(), Is.GreaterThan(3), "Soft shading must vary within the region");
            var repeat = SpaceRegionRadarMesh.Build(region, contour);
            Assert.That(repeat.Select(v => (v.Position, v.Color)), Is.EqualTo(mesh.Select(v => (v.Position, v.Color))));
        }
    }
}

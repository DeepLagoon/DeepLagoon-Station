using System.Collections.Generic;
using System.Numerics;
using Content.Client.Shuttles.UI;
using NUnit.Framework;
using Robust.Shared.Maths;

namespace Content.Tests.Client;

[TestFixture]
public sealed class RadarSignatureLayoutTests
{
    [Test]
    public void LabelsConnectedByAnOverlapFormOneGroup()
    {
        var signatures = new List<ShuttleNavControl.RadarSignature>
        {
            NewSignature(100, 18),
            NewSignature(0, 36),
            NewSignature(0, 0),
            NewSignature(0, 18),
        };
        var order = new List<int>();
        var ends = new List<int>();

        ShuttleNavControl.GroupOverlappingSignatures(signatures, new List<bool>(), order, ends);

        Assert.That(order, Is.EqualTo(new[] { 0, 1, 3, 2 }));
        Assert.That(ends, Is.EqualTo(new[] { 3, 4 }));
    }

    [Test]
    public void NearbyMarkersCanKeepSeparateFullLabels()
    {
        var viewport = new Vector2(400, 240);
        var first = NewSignature(200, 110);
        var second = NewSignature(197, 114);
        var occupied = new List<UIBox2> { MarkerBounds(first.Point), MarkerBounds(second.Point) };
        var size = new Vector2(80, 48);

        Assert.That(ShuttleNavControl.TryFindLabelBounds(first.Point, size, 12, 10, viewport, null, 1, occupied, out var right), Is.True);
        occupied.Add(right);
        Assert.That(ShuttleNavControl.TryFindLabelBounds(second.Point, size, 12, 10, viewport, null, 1, occupied, out var left), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(right.Left, Is.GreaterThan(first.Point.X));
            Assert.That(left.Right, Is.LessThan(second.Point.X));
            Assert.That(left.Intersects(right), Is.False);
            Assert.That(left.Size, Is.EqualTo(size));
        });
    }

    [Test]
    public void LabelAvoidsAnotherMarkerWithNoLabelYet()
    {
        var viewport = new Vector2(400, 240);
        var first = NewSignature(200, 110);
        var second = NewSignature(240, 110);
        var occupied = new List<UIBox2> { MarkerBounds(first.Point), MarkerBounds(second.Point) };

        Assert.That(ShuttleNavControl.TryFindLabelBounds(first.Point, first.Bounds.Size, 20, 10, viewport, null, 1,
            occupied, out var bounds), Is.True);
        Assert.That(bounds.Right, Is.LessThan(first.Point.X));
    }

    [Test]
    public void LabelFitsInsideCurvedRadarEdge()
    {
        var viewport = new Vector2(400, 240);
        var center = viewport / 2;
        const float radius = 116;
        var signature = NewSignature(306, 88);
        var occupied = new List<UIBox2> { MarkerBounds(signature.Point) };

        Assert.That(ShuttleNavControl.TryFindLabelBounds(signature.Point, signature.Bounds.Size, 20, 10, viewport, radius, 1,
            occupied, out var bounds), Is.True);
        var farCorner = Vector2.Abs(bounds.Center - center) + bounds.Size / 2;
        Assert.That(farCorner.Length(), Is.LessThanOrEqualTo(radius));
    }

    [Test]
    public void FullyOccupiedRadarFallsBackToGrouping()
    {
        var viewport = new Vector2(400, 240);
        var signature = NewSignature(200, 110);
        var occupied = new List<UIBox2> { UIBox2.FromDimensions(Vector2.Zero, viewport) };

        Assert.That(ShuttleNavControl.TryFindLabelBounds(signature.Point, signature.Bounds.Size, 20, 10, viewport, null, 1,
            occupied, out _), Is.False);
    }

    [TestCase(200, 120, true, true)]
    [TestCase(350, 160, true, false)]
    [TestCase(350, 160, false, true)]
    [TestCase(401, 160, false, false)]
    public void SignatureVisibilityMatchesTheViewportShape(int x, int y, bool circular, bool visible)
    {
        var size = new Vector2(400, 240);
        Assert.That(ShuttleNavControl.SignatureInView(new Vector2(x, y), size, circular, 116), Is.EqualTo(visible));
    }

    [TestCase(false, 198f)]
    [TestCase(true, 114f)]
    public void OffscreenSignatureAnchorsToRadarEdgeInItsDirection(bool circular, float expectedOffset)
    {
        var viewport = new Vector2(400, 240);
        var point = new Vector2(500, 120);

        Assert.That(ShuttleNavControl.TryGetOffscreenSignaturePoint(point, viewport, circular ? 116 : null, 2, out var edge), Is.True);
        Assert.That(edge, Is.EqualTo(viewport / 2 + new Vector2(expectedOffset, 0)));
    }

    private static ShuttleNavControl.RadarSignature NewSignature(float x, float y) =>
        new(default, new Vector2(x, y), "ship", "ship", null, null, null, Color.White, UIBox2.FromDimensions(x, y, 60, 20),
            new Vector2(60, 20));

    private static UIBox2 MarkerBounds(Vector2 point) => new(point - new Vector2(7.5f, 5), point + new Vector2(7.5f, 10));
}

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

    [TestCase(200, 120, true, true)]
    [TestCase(350, 160, true, false)]
    [TestCase(350, 160, false, true)]
    [TestCase(401, 160, false, false)]
    public void SignatureVisibilityMatchesTheViewportShape(int x, int y, bool circular, bool visible)
    {
        var size = new Vector2(400, 240);
        Assert.That(ShuttleNavControl.SignatureInView(new Vector2(x, y), size, circular, 116), Is.EqualTo(visible));
    }

    private static ShuttleNavControl.RadarSignature NewSignature(float x, float y) =>
        new(default, new Vector2(x, y), "ship", "ship", null, null, null, Color.White, UIBox2.FromDimensions(x, y, 60, 20));
}

using System.IO;
using System.Linq;
using Content.Shared.Humanoid.Markings;
using NUnit.Framework;
using Robust.Shared.Maths;

namespace Content.Tests._DeepLagoon;

[TestFixture]
public sealed class MarkingGlowTests
{
    private static bool[] Glow(Marking marking) => Enumerable.Range(0, marking.MarkingColors.Count)
        .Select(marking.IsUnshaded).ToArray();

    [TestCase("Test@#FFFFFFFF,#FFFFFFFF,#FFFFFFFF", false)]
    [TestCase("Test@#FFFFFFFF,#FFFFFFFF,#FFFFFFFF@7@1", true)]
    public void LegacySettingsApplyToEveryColor(string saved, bool expected)
    {
        var marking = Marking.ParseFromDbString(saved)!;
        Assert.That(Glow(marking), Is.EqualTo(new[] { expected, expected, expected }));
        marking.SetUnshaded(1, !expected);
        Assert.That(Glow(marking), Is.EqualTo(new[] { expected, !expected, expected }));
        Assert.That(Glow(Marking.ParseFromDbString(marking.ToString())!), Is.EqualTo(Glow(marking)));
    }

    [Test]
    public void MixedGlowSurvivesCopiesRecoloringAndDatabaseStorage()
    {
        var marking = new Marking("Test", 3) { DrawOrder = 9 };
        marking.SetUnshaded(1, true);
        var copy = new Marking(marking);
        var stored = Marking.ParseFromDbString(marking.ToString())!;
        Assert.That(stored, Is.EqualTo(marking));
        Assert.That(stored.DrawOrder, Is.EqualTo(9));
        copy.SetUnshaded(0, true);
        copy.SetColor(1, Color.Red);
        Assert.That(Glow(marking), Is.EqualTo(new[] { false, true, false }), "Copies must not share the glow list");
        Assert.That(copy, Is.Not.EqualTo(marking));
        var recolored = new Marking("Other", 3);
        recolored.CopyGlowFrom(stored);
        Assert.That(Glow(recolored), Is.EqualTo(Glow(marking)));
        recolored.CopyGlowFrom(stored, allowed: false);
        Assert.That(recolored.HasGlow, Is.False);
        stored.SetUnshaded(false);
        Assert.That(Glow(stored), Is.EqualTo(new[] { false, false, false }));
        Assert.That(stored.HasGlow, Is.False);
    }

    [Test]
    public void NetworkSerializationPreservesIndividualLayers()
    {
        var marking = new Marking("Test", 3);
        marking.SetUnshaded(1, true);
        var serializer = new NetSerializer.Serializer(new[] { typeof(Marking) });
        using var stream = new MemoryStream();
        serializer.Serialize(stream, marking);
        stream.Position = 0;
        var received = (Marking) serializer.Deserialize(stream);
        Assert.That(Glow(received), Is.EqualTo(new[] { false, true, false }));
        Assert.That(received, Is.EqualTo(marking));
    }

    [TestCase("Test@#FFFFFFFF,#FFFFFFFF@0@0@1")]
    [TestCase("Test@#FFFFFFFF,#FFFFFFFF@0@0@1x")]
    [TestCase("Test@#FFFFFFFF,#FFFFFFFF@0@0@101")]
    public void InvalidLayerMasksAreRejected(string saved) => Assert.That(Marking.ParseFromDbString(saved), Is.Null);
}

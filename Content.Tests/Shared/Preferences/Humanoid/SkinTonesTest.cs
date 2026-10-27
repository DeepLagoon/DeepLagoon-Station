using Content.Shared.Humanoid;
using NUnit.Framework;
using Robust.Shared.Maths;

namespace Content.Tests.Shared.Preferences.Humanoid;

[TestFixture]
public sealed class SkinTonesTest
{
    [Test]
    public void TestHumanSkinToneValidity()
    {
        for (var i = 0; i <= 100; i++)
        {
            var color = SkinColor.HumanSkinTone(i);
            Assert.That(SkinColor.VerifyHumanSkinTone(color));
        }
    }

    [Test]
    public void TestDefaultSkinToneValid()
    {
        Assert.That(SkinColor.VerifyHumanSkinTone(SkinColor.ValidHumanSkinTone));
    }

    [TestCase("#FAF2F8")]
    [TestCase("#FF0000")]
    [TestCase("#00FF00")]
    [TestCase("#0000FF")]
    [TestCase("#FFFFFF")]
    [TestCase("#000000")]
    [TestCase("#F2EEEE")]
    public void TintedHueValidationIsValidAndIdempotent(string value)
    {
        var color = Color.FromHex(value);
        var validated = SkinColor.ValidTintedHuesSkinTone(color);
        Assert.That(SkinColor.VerifyTintedHues(validated), Is.True);
        Assert.That(SkinColor.ValidTintedHuesSkinTone(validated), Is.EqualTo(validated));
    }
}

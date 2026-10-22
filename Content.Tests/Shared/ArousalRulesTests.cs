using Content.Shared._DeepLagoon.InteractionPanel;
using NUnit.Framework;

namespace Content.Tests.Shared;

[TestFixture]
public sealed class ArousalRulesTests
{
    [TestCase(20f, -5f, 15f, false)]
    [TestCase(20f, -100f, 0f, false)]
    [TestCase(90f, 10f, 0f, true)]
    [TestCase(0f, 500f, 0f, true)]
    [TestCase(10f, 5f, 15f, false)]
    public void SignedAdjustmentsClampAndDischargeOnce(float current, float amount, float expected, bool discharge)
    {
        Assert.That(InteractionPanelArousalRules.Add(ref current, amount, 100), Is.EqualTo(discharge));
        Assert.That(current, Is.EqualTo(expected));
    }

    [Test]
    public void InvalidInputCannotPoisonTheMeter()
    {
        var current = 25f;
        Assert.That(InteractionPanelArousalRules.Add(ref current, float.NaN, 100), Is.False);
        Assert.That(InteractionPanelArousalRules.Add(ref current, 5, float.PositiveInfinity), Is.False);
        Assert.That(current, Is.EqualTo(25));
        current = float.NaN;
        InteractionPanelArousalRules.Add(ref current, 5, 100);
        Assert.That(current, Is.EqualTo(5));
    }
}

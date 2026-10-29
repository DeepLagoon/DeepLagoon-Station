using Content.Shared._DeepLagoon.InteractionPanel;
using NUnit.Framework;

namespace Content.Tests.Shared;

[TestFixture]
public sealed class ArousalRulesTests
{
    [TestCase(10f, 0f, false, 0f)]
    [TestCase(10f, 50f, false, 5f)]
    [TestCase(10f, 100f, false, 10f)]
    [TestCase(10f, 300f, false, 30f)]
    [TestCase(10f, 900f, false, 30f)]
    [TestCase(0f, 300f, false, 0f)]
    [TestCase(0f, 100f, true, 0f)]
    [TestCase(0f, 150f, true, 1f)]
    [TestCase(0f, 300f, true, 4f)]
    [TestCase(-10f, 0f, true, -10f)]
    [TestCase(-10f, 300f, true, -10f)]
    [TestCase(float.NaN, 100f, true, 0f)]
    [TestCase(10f, float.PositiveInfinity, true, 10f)]
    public void GainScalesOnlyPositiveAmountsAndRequiresInteractionsForNeutralGain(float amount, float multiplier, bool interaction, float expected)
    {
        Assert.That(InteractionPanelArousalRules.Gain(amount, multiplier, interaction), Is.EqualTo(expected));
    }

    [TestCase(30f, 100f, true, 100f, false)]
    [TestCase(30.01f, 100f, true, 100f, true)]
    [TestCase(60f, 200f, true, 100f, false)]
    [TestCase(61f, 200f, true, 100f, true)]
    [TestCase(90f, 100f, false, 100f, false)]
    [TestCase(90f, 100f, true, 0f, false)]
    [TestCase(90f, 100f, true, -1f, false)]
    [TestCase(float.NaN, 100f, true, 100f, false)]
    [TestCase(90f, 0f, true, 100f, false)]
    public void MoansRequireEnabledChanceAndStrictRelativeThreshold(float current, float maximum, bool enabled, float chance, bool expected)
    {
        Assert.That(InteractionPanelArousalRules.CanMoan(current, maximum, enabled, chance), Is.EqualTo(expected));
    }

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

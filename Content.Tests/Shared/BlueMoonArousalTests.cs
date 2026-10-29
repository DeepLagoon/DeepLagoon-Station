using System;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared.Preferences;
using NUnit.Framework;

namespace Content.Tests.Shared;

[TestFixture]
public sealed class BlueMoonArousalTests
{
    [TestCase(25, 75)]
    [TestCase(100, 300)]
    [TestCase(200, 600)]
    [TestCase(-1, 75)]
    [TestCase(float.NaN, 300)]
    public void ToleranceMatchesBlueMoonAndRejectsInvalidValues(float tolerance, float expected) =>
        Assert.That(BlueMoonArousalRules.Threshold(tolerance), Is.EqualTo(expected));

    [TestCase(0, 0)]
    [TestCase(149, 0)]
    [TestCase(150, 0)]
    [TestCase(225, 32)]
    [TestCase(300, 100)]
    [TestCase(600, 100)]
    public void MoanCurveUsesReferenceBezier(float value, float expected) =>
        Assert.That(BlueMoonArousalRules.MoanChance(value, 300), Is.EqualTo(expected));

    [TestCase(0, 2, 1, 50, 0)]
    [TestCase(10, 2, 1, 50, 2)]
    [TestCase(10, 3, 2, 50, 6)]
    [TestCase(250, 2, 1, 50, 50)]
    [TestCase(10000, 2, 1, 50, 50)]
    [TestCase(-10, 2, 1, 50, 0)]
    public void FluidProductionIsBoundedAndTimeBased(float seconds, float rate, float multiplier, float max, float expected) =>
        Assert.That(BlueMoonArousalRules.FluidAmount(seconds, rate, multiplier, max), Is.EqualTo(expected));

    [TestCase(15, 2, 30, false, 28.5f)]
    [TestCase(15, 8, 90, true, 90)]
    [TestCase(-1, 200, 30, false, 30)]
    public void RecoveryConvertsByondDeciseconds(int potency, int count, float randomSeconds, bool extended, float expected) =>
        Assert.That(BlueMoonArousalRules.RecoverySeconds(potency, count, randomSeconds, extended), Is.EqualTo(expected));

    [Test]
    public void PregnancyAndGrowthAreOptInAndProfileCopiesAreIndependent()
    {
        var original = new HumanoidCharacterProfile();
        Assert.Multiple(() =>
        {
            Assert.That(original.ArousalPreferences.FluidGrowth, Is.False);
            Assert.That(original.ArousalPreferences.Pregnancy, Is.False);
            Assert.That(original.ArousalPreferences.ReceiveFluids, Is.False);
        });
        var edited = original.WithArousalPreferences(original.ArousalPreferences.WithOption("fluid-growth", 1)
            .WithOption("pregnancy", 1).WithOption("fertility", 900));
        Assert.That(edited.ArousalPreferences.Fertility, Is.EqualTo(100));
        Assert.That(original.ArousalPreferences.Pregnancy, Is.False);
        var copy = edited.Clone();
        copy.ArousalPreferences.Fertility = 0;
        Assert.That(edited.ArousalPreferences.Fertility, Is.EqualTo(100));
        Assert.That(BlueMoonArousalRules.FertilizationChance(100, 100, 0), Is.EqualTo(80));
        Assert.That(BlueMoonArousalRules.FertilizationChance(100, 0, 0), Is.Zero);
        Assert.That(new BlueMoonPreferences { GenitalFluid = "Toxin" }.Sanitize().GenitalFluid, Is.EqualTo("DLSemen"));
    }
}

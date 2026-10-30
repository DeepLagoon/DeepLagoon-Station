using Content.Server._DeepLagoon.Salvage;
using Content.Shared.Salvage;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class ExpeditionLootBalanceTests
{
    [TestCase(DifficultyRating.Minimal, 1)]
    [TestCase(DifficultyRating.Minor, 1)]
    [TestCase(DifficultyRating.Moderate, 2)]
    [TestCase(DifficultyRating.Hazardous, 3)]
    [TestCase(DifficultyRating.Extreme, 3)]
    public void OrdinaryLootIsCapped(DifficultyRating difficulty, int maximum)
    {
        Assert.That(ExpeditionLootSystem.MaximumTier(difficulty), Is.EqualTo(maximum));
    }

    [TestCase(DifficultyRating.Minimal)]
    [TestCase(DifficultyRating.Minor)]
    [TestCase(DifficultyRating.Moderate)]
    [TestCase(DifficultyRating.Hazardous)]
    public void LowerDifficultiesCannotRollRareLootEvenOnTheBestRoll(DifficultyRating difficulty)
    {
        Assert.That(ExpeditionLootSystem.RollRareTier(difficulty, 0, 1.0 / 168, 1.0 / 720), Is.Zero);
    }

    [Test]
    public void RareIntervalsAreDisjointAndUseTheServerCadence()
    {
        const double t4 = 1.0 / 168;
        const double t5 = 1.0 / 720;
        Assert.That(ExpeditionLootSystem.RollRareTier(DifficultyRating.Extreme, 0, t4, t5), Is.EqualTo(5));
        Assert.That(ExpeditionLootSystem.RollRareTier(DifficultyRating.Extreme, t5, t4, t5), Is.EqualTo(4));
        Assert.That(ExpeditionLootSystem.RollRareTier(DifficultyRating.Extreme, t5 + t4, t4, t5), Is.Zero);
        Assert.That(ExpeditionLootSystem.RollRareTier(DifficultyRating.Extreme, 0.999, t4, t5), Is.Zero);
        Assert.That(1 / (24 * t4), Is.EqualTo(7).Within(0.0001));
        Assert.That(1 / (24 * t5), Is.EqualTo(30).Within(0.0001));
    }
}

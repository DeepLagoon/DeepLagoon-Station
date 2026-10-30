using Content.Server._DeepLagoon.SpaceEnvironment;
using Content.Server.Xenoarchaeology.XenoArtifacts;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class SpaceSurveyResearchRewardTests
{
    [TestCase(100f, 0.25f, 0f, 6825)]
    [TestCase(500f, 1f, 0f, 9750)]
    [TestCase(500f, 1f, 1f, 19500)]
    [TestCase(1000f, 0.25f, 1f, 19500)]
    [TestCase(1000f, 1f, 0.5f, 19500)]
    [TestCase(2000f, 1f, 1f, 39000)]
    public void RewardScalesWithIntensityAndEquivalentArea(float radius, float aspect, float intensity, int expected)
    {
        var region = new SpaceRegion { Radius = radius, Aspect = aspect, Intensity = intensity };
        Assert.That(SpaceSurveyResearchReward.Calculate(region), Is.EqualTo(expected));
        Assert.That(SpaceSurveyResearchReward.Calculate(region), Is.GreaterThanOrEqualTo(new ArtifactComponent().PointsPerNode));
    }

    [Test]
    public void EqualAreaEllipsesGiveEqualRewards()
    {
        var circle = new SpaceRegion { Radius = 500, Aspect = 1, Intensity = 0.5f };
        var ellipse = new SpaceRegion { Radius = 1000, Aspect = 0.25f, Intensity = 0.5f };
        Assert.That(SpaceSurveyResearchReward.Calculate(circle), Is.EqualTo(SpaceSurveyResearchReward.Calculate(ellipse)));
    }
}

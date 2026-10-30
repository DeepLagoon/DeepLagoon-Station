using Content.Shared._DeepLagoon.SpaceEnvironment;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

public static class SpaceSurveyResearchReward
{
    // The base value of an artifact node in ArtifactComponent.
    public const int DefaultBasePoints = 6500;
    private const double ReferenceRadius = 1000;

    public static int Calculate(SpaceRegion region, int basePoints = DefaultBasePoints)
    {
        var equivalentRadius = region.Radius * Math.Sqrt(region.Aspect);
        return (int) Math.Ceiling(Math.Clamp(basePoints, 1, 1000000)
            * (1 + region.Intensity) * (1 + equivalentRadius / ReferenceRadius));
    }
}

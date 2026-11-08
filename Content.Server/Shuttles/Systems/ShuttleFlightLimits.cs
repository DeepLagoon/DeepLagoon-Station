using System;
using System.Numerics;
using Content.Server.Shuttles.Components;

namespace Content.Server.Shuttles.Systems;

/// <summary>Part-tier flight envelope. Speeds use the navigation console's world units per second.</summary>
public static class ShuttleFlightLimits
{
    public const float MaximumAccelerationTier = 4f;
    private const float MaximumSpeedTier = 6f;
    private const float ProfileSpeedPerTier = 10f;
    private const float HighTierSpeedBonus = 100f;
    private const float MicroShuttleMass = 20f;
    private const float FullBonusMass = 50f;
    private const float LightShuttleMass = 100f;
    private const float MediumShuttleMass = 300f;
    private const float HeavyShuttleMass = 350f;
    private const float LightShuttleAccelerationBonus = 0.5f;

    public static float PartTier(float baseThrust, float thrust, float multiplier)
    {
        if (baseThrust <= 0f || thrust <= 0f || multiplier <= 1f)
            return 1f;
        var tier = 1f + MathF.Log(thrust / baseThrust) / MathF.Log(multiplier);
        return float.IsFinite(tier) ? MathF.Max(1f, tier) : 1f;
    }

    public static float MaxSpeed(float tier, float mass)
    {
        var speedTier = Math.Clamp(tier, 1f, MaximumSpeedTier);
        var profileSpeed = 45f + ProfileTierSpeedOffset(tier) +
            HighTierSpeedBonus * Math.Clamp(tier - MaximumAccelerationTier, 0f, 1f);
        var heavySpeed = float.Lerp(70f, 130f, (speedTier - 1f) / (MaximumSpeedTier - 1f));
        var fullHullSpeed = float.Lerp(profileSpeed, heavySpeed, HeavyHullScale(mass));
        return float.Lerp(BaseMaxSpeed(speedTier), fullHullSpeed, SmallHullBonusScale(mass));
    }

    private static float ProfileTierSpeedOffset(float tier) =>
        ProfileSpeedPerTier * (Math.Clamp(tier, 1f, MaximumAccelerationTier) - 1f);

    private static float SmallHullBonusScale(float mass) =>
        Math.Clamp((mass - MicroShuttleMass) / (FullBonusMass - MicroShuttleMass), 0f, 1f);

    private static float HeavyHullScale(float mass) =>
        Math.Clamp((mass - MediumShuttleMass) / (HeavyShuttleMass - MediumShuttleMass), 0f, 1f);

    public static float AccelerationProfileWeight(float mass) => SmallHullBonusScale(mass) * (1f - HeavyHullScale(mass));

    private static float BaseMaxSpeed(float tier) => 35f + 10f * (MathF.Max(1f, tier) - 1f);

    private static float BaseAccelerationTime(float tier)
    {
        tier = MathF.Max(1f, tier);
        if (tier <= 2f) return 20f - 5f * (tier - 1f);
        if (tier <= 3f) return 15f - 5f * (tier - 2f);
        if (tier <= 4f) return 10f - 2f * (tier - 3f);
        return MathF.Max(2f, 8f * MathF.Pow(0.8f, tier - 4f));
    }

    public static float DirectionTier(ShuttleComponent shuttle, Vector2 direction, float accelerationProfileWeight = 0f)
    {
        var x = direction.X > 0f ? 1 : 3;
        var y = direction.Y > 0f ? 2 : 0;
        var weightX = MathF.Abs(direction.X);
        var weightY = MathF.Abs(direction.Y);
        var total = weightX * shuttle.BaseLinearThrust[x] + weightY * shuttle.BaseLinearThrust[y];
        if (total <= 0f)
            return 1f;

        var ratedX = float.Lerp(shuttle.RatedBaseLinearThrust[x], shuttle.AccelerationRatedBaseLinearThrust[x], accelerationProfileWeight);
        var ratedY = float.Lerp(shuttle.RatedBaseLinearThrust[y], shuttle.AccelerationRatedBaseLinearThrust[y], accelerationProfileWeight);
        return MathF.Max(1f, (weightX * ratedX + weightY * ratedY) / total);
    }

    public static float GridMaxSpeed(ShuttleComponent shuttle, float mass)
    {
        var tier = 1f;
        for (var i = 0; i < 4; i++)
        {
            if (shuttle.BaseLinearThrust[i] > 0f)
                tier = MathF.Max(tier, shuttle.RatedBaseLinearThrust[i] / shuttle.BaseLinearThrust[i]);
        }
        return MaxSpeed(tier, mass);
    }

    public static Vector2 GetFlightThrust(Vector2 force, float mass, float accelerationTier, float speed, bool braking = false)
    {
        // The previous speed curve keeps the baseline acceleration independent of the new speed cap.
        var maximum = mass * BaseMaxSpeed(accelerationTier) / BaseAccelerationTime(accelerationTier);
        var limited = force.LengthSquared() > maximum * maximum && maximum > 0f
            ? Vector2.Normalize(force) * maximum : force;

        if (braking)
            return limited;

        var lightness = Math.Clamp((HeavyShuttleMass - mass) / (HeavyShuttleMass - LightShuttleMass), 0f, 1f);
        var massBonus = 1f + LightShuttleAccelerationBonus * lightness * SmallHullBonusScale(mass);
        var speedAboveTierOffset = speed - ProfileTierSpeedOffset(accelerationTier);
        var stageScale = speedAboveTierOffset < 20f ? 1f : speedAboveTierOffset < 30f ? 0.8f : 0.6f;
        return limited * (massBonus * float.Lerp(1f, stageScale, AccelerationProfileWeight(mass)));
    }

    /// <summary>Limit the resulting velocity, including diagonal thrust and a partial final step.</summary>
    public static Vector2 LimitThrust(Vector2 velocity, Vector2 force, float mass, float step, float maxSpeed)
    {
        if (mass <= 0f || step <= 0f || force == Vector2.Zero)
            return Vector2.Zero;
        maxSpeed = MathF.Max(0f, maxSpeed);
        var next = velocity + force * (step / mass);
        if (next.LengthSquared() <= maxSpeed * maxSpeed)
            return force;
        var target = maxSpeed == 0f ? Vector2.Zero : Vector2.Normalize(next) * maxSpeed;
        var limited = (target - velocity) * (mass / step);
        // Changing to a lower tier's direction must brake using available thrust.
        return limited.LengthSquared() > force.LengthSquared()
            ? Vector2.Normalize(limited) * force.Length() : limited;
    }
}

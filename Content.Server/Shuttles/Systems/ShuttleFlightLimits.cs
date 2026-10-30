using System;
using System.Numerics;
using Content.Server.Shuttles.Components;

namespace Content.Server.Shuttles.Systems;

/// <summary>Part-tier flight envelope. Speeds use the navigation console's world units per second.</summary>
public static class ShuttleFlightLimits
{
    public static float PartTier(float baseThrust, float thrust, float multiplier)
    {
        if (baseThrust <= 0f || thrust <= 0f || multiplier <= 1f)
            return 1f;
        var tier = 1f + MathF.Log(thrust / baseThrust) / MathF.Log(multiplier);
        return float.IsFinite(tier) ? MathF.Max(1f, tier) : 1f;
    }

    public static float MaxSpeed(float tier) => 35f + 10f * (MathF.Max(1f, tier) - 1f);

    /// <summary>Minimum seconds from rest to the tier's maximum with sufficient thrust.</summary>
    public static float AccelerationTime(float tier)
    {
        tier = MathF.Max(1f, tier);
        if (tier <= 2f) return 20f - 5f * (tier - 1f);
        if (tier <= 3f) return 15f - 5f * (tier - 2f);
        if (tier <= 4f) return 10f - 2f * (tier - 3f);
        return MathF.Max(2f, 8f * MathF.Pow(0.8f, tier - 4f));
    }

    public static float DirectionTier(ShuttleComponent shuttle, Vector2 direction)
    {
        var x = direction.X > 0f ? 1 : 3;
        var y = direction.Y > 0f ? 2 : 0;
        var weightX = MathF.Abs(direction.X);
        var weightY = MathF.Abs(direction.Y);
        var total = weightX * shuttle.BaseLinearThrust[x] + weightY * shuttle.BaseLinearThrust[y];
        return total <= 0f ? 1f : MathF.Max(1f,
            (weightX * shuttle.RatedBaseLinearThrust[x] + weightY * shuttle.RatedBaseLinearThrust[y]) / total);
    }

    public static float GridMaxSpeed(ShuttleComponent shuttle)
    {
        var tier = 1f;
        for (var i = 0; i < 4; i++)
        {
            if (shuttle.BaseLinearThrust[i] > 0f)
                tier = MathF.Max(tier, shuttle.RatedBaseLinearThrust[i] / shuttle.BaseLinearThrust[i]);
        }
        return MaxSpeed(tier);
    }

    public static Vector2 LimitAcceleration(Vector2 force, float mass, float tier)
    {
        var maximum = mass * MaxSpeed(tier) / AccelerationTime(tier);
        return force.LengthSquared() > maximum * maximum && maximum > 0f
            ? Vector2.Normalize(force) * maximum : force;
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

using System;
using System.Numerics;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class ShuttleFlightLimitsTests
{
    [TestCase(1, 35, 20)]
    [TestCase(2, 45, 15)]
    [TestCase(3, 55, 10)]
    [TestCase(4, 65, 8)]
    [TestCase(5, 75, 6.4f)]
    [TestCase(6, 85, 5.12f)]
    public void PartsDefineSpeedAndMinimumAccelerationTime(int tier, float speed, float seconds)
    {
        Assert.That(ShuttleFlightLimits.PartTier(100, 100 * MathF.Pow(1.25f, tier - 1), 1.25f), Is.EqualTo(tier).Within(0.001));
        Assert.That(ShuttleFlightLimits.MaxSpeed(tier), Is.EqualTo(speed));
        Assert.That(ShuttleFlightLimits.AccelerationTime(tier), Is.EqualTo(seconds).Within(0.001));
    }

    [TestCase(1, false)]
    [TestCase(2, true)]
    [TestCase(3, false)]
    [TestCase(4, true)]
    [TestCase(5, true)]
    public void ExcessEnginesAndPilotBoostCannotBypassEnvelope(int tier, bool diagonal)
    {
        const float mass = 100f;
        const float step = 1f / 60;
        var direction = Vector2.Normalize(diagonal ? Vector2.One : Vector2.UnitX);
        var velocity = Vector2.Zero;
        var maximum = ShuttleFlightLimits.MaxSpeed(tier);
        for (var i = 0; i < 2400; i++)
        {
            var force = ShuttleFlightLimits.LimitAcceleration(direction * 1e9f, mass, tier);
            force = ShuttleFlightLimits.LimitThrust(velocity, force, mass, step, maximum);
            velocity += force * (step / mass);
            Assert.That(velocity.Length(), Is.LessThanOrEqualTo(maximum + 0.001f));
            Assert.That(velocity.Length(), Is.LessThanOrEqualTo(maximum / ShuttleFlightLimits.AccelerationTime(tier) * (i + 1) * step + 0.005f));
        }
        Assert.That(velocity.Length(), Is.EqualTo(maximum).Within(0.001));
    }

    [Test]
    public void MixedPartsAreWeightedByBaseThrustAndDirection()
    {
        var shuttle = new ShuttleComponent();
        shuttle.BaseLinearThrust[1] = 1000;
        shuttle.RatedBaseLinearThrust[1] = 1000;
        // One small T4 engine cannot give ten T1 engines a T4 speed limit.
        shuttle.BaseLinearThrust[1] += 100;
        shuttle.RatedBaseLinearThrust[1] += 400;
        Assert.That(ShuttleFlightLimits.MaxSpeed(ShuttleFlightLimits.DirectionTier(shuttle, Vector2.UnitX)), Is.EqualTo(35 + 30f / 11).Within(0.001));
        Assert.That(ShuttleFlightLimits.DirectionTier(shuttle, Vector2.UnitY), Is.EqualTo(1));
        Assert.That(ShuttleFlightLimits.GridMaxSpeed(shuttle), Is.LessThan(45));
    }

    [Test]
    public void ZeroSpeedSettingAndBrakingRemainFinite()
    {
        Assert.That(ShuttleFlightLimits.LimitThrust(Vector2.Zero, Vector2.One, 100, 1f / 60, 0), Is.EqualTo(Vector2.Zero));
        var braking = new Vector2(-100, 0);
        Assert.That(ShuttleFlightLimits.LimitThrust(new Vector2(10, 0), braking, 100, 1f / 60, 35), Is.EqualTo(braking));
    }
}

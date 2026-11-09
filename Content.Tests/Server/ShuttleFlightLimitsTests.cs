using System;
using System.Numerics;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class ShuttleFlightLimitsTests
{
    [TestCase(1, 35, 20, 70)]
    [TestCase(2, 45, 15, 82)]
    [TestCase(3, 55, 10, 94)]
    [TestCase(4, 65, 8, 106)]
    [TestCase(5, 75, 6.4f, 118)]
    [TestCase(6, 85, 5.12f, 130)]
    [TestCase(8, 105, 3.2768f, 130)]
    public void PartsDefineSpeedAndRetainHeavyShuttleAcceleration(int tier, float previousSpeed, float previousSeconds, float speedLimit)
    {
        const float heavyMass = 350f;
        Assert.That(ShuttleFlightLimits.PartTier(100, 100 * MathF.Pow(1.25f, tier - 1), 1.25f), Is.EqualTo(tier).Within(0.001));
        Assert.That(ShuttleFlightLimits.MaxSpeed(tier, heavyMass), Is.EqualTo(speedLimit).Within(0.001));
        var force = ShuttleFlightLimits.GetFlightThrust(Vector2.UnitX * 1e9f, heavyMass, tier, 100f);
        Assert.That(force.X / heavyMass, Is.EqualTo(previousSpeed / previousSeconds).Within(0.001));
    }

    [TestCase(8, 1f)]
    [TestCase(20, 1f)]
    [TestCase(35, 1.25f)]
    [TestCase(50, 1.5f)]
    [TestCase(100, 1.5f)]
    [TestCase(225, 1.25f)]
    [TestCase(350, 1f)]
    [TestCase(600, 1f)]
    public void MassBonusAppliesToAvailableThrustAndAccelerationCap(float mass, float multiplier)
    {
        const float baseAcceleration = 35f / 20f;
        var available = Vector2.UnitX * mass;
        var excess = available * 100f;
        Assert.That(ShuttleFlightLimits.GetFlightThrust(available, mass, 1, 0f).X, Is.EqualTo(mass * multiplier).Within(0.001));
        Assert.That(ShuttleFlightLimits.GetFlightThrust(excess, mass, 1, 0f).X / mass, Is.EqualTo(baseAcceleration * multiplier).Within(0.001));
        Assert.That(ShuttleFlightLimits.GetFlightThrust(excess, mass, 1, 100f, braking: true).X / mass, Is.EqualTo(baseAcceleration).Within(0.001));
    }

    [TestCase(8, 35, 85)]
    [TestCase(20, 35, 85)]
    [TestCase(35, 40, 130)]
    [TestCase(50, 45, 175)]
    [TestCase(225, 45, 175)]
    [TestCase(300, 45, 175)]
    [TestCase(325, 57.5f, 152.5f)]
    [TestCase(350, 70, 130)]
    public void SpeedLimitsBlendBetweenHullClasses(float mass, float tierOneSpeed, float tierSixSpeed)
    {
        Assert.That(ShuttleFlightLimits.MaxSpeed(1, mass), Is.EqualTo(tierOneSpeed).Within(0.001));
        Assert.That(ShuttleFlightLimits.MaxSpeed(6, mass), Is.EqualTo(tierSixSpeed).Within(0.001));
        Assert.That(ShuttleFlightLimits.MaxSpeed(8, mass), Is.EqualTo(tierSixSpeed).Within(0.001));
    }

    [TestCase(1, 20, 30, 45)]
    [TestCase(2, 30, 40, 55)]
    [TestCase(3, 40, 50, 65)]
    [TestCase(4, 50, 60, 75)]
    public void FlightProfileStepsThroughSpeedBands(int tier, float firstThreshold, float secondThreshold, float speedLimit)
    {
        const float mass = 225f;
        var available = new Vector2(1e6f, 0f);
        var takeoff = ShuttleFlightLimits.GetFlightThrust(available, mass, tier, 0f).X;
        Assert.That(ShuttleFlightLimits.MaxSpeed(tier, mass), Is.EqualTo(speedLimit));
        Assert.That(ShuttleFlightLimits.GetFlightThrust(available, mass, tier, firstThreshold - 0.1f).X, Is.EqualTo(takeoff));
        Assert.That(ShuttleFlightLimits.GetFlightThrust(available, mass, tier, firstThreshold).X, Is.EqualTo(takeoff * 0.8f).Within(0.001));
        Assert.That(ShuttleFlightLimits.GetFlightThrust(available, mass, tier, secondThreshold).X, Is.EqualTo(takeoff * 0.6f).Within(0.001));
    }

    [TestCase(1, false, 100f)]
    [TestCase(2, true, 100f)]
    [TestCase(3, false, 100f)]
    [TestCase(4, true, 100f)]
    [TestCase(5, true, 100f)]
    [TestCase(6, true, 8f)]
    public void ExcessEnginesAndPilotBoostCannotBypassEnvelope(int tier, bool diagonal, float mass)
    {
        const float step = 1f / 60;
        var direction = Vector2.Normalize(diagonal ? Vector2.One : Vector2.UnitX);
        var velocity = Vector2.Zero;
        var maximum = ShuttleFlightLimits.MaxSpeed(tier, mass);
        var accelerationTier = mass == 100f ? Math.Min(tier, 4) : tier;
        var available = direction * 1e9f;
        var maxAccel = ShuttleFlightLimits.GetFlightThrust(available, mass, accelerationTier, 0f).Length() / mass;
        for (var i = 0; i < 2400; i++)
        {
            var thrust = ShuttleFlightLimits.GetFlightThrust(available, mass, accelerationTier, velocity.Length());
            var force = ShuttleFlightLimits.LimitThrust(velocity, thrust, mass, step, maximum);
            velocity += force * (step / mass);
            Assert.That(velocity.Length(), Is.LessThanOrEqualTo(maximum + 0.001f));
            Assert.That(velocity.Length(), Is.LessThanOrEqualTo(maxAccel * (i + 1) * step + 0.005f));
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
        Assert.That(ShuttleFlightLimits.MaxSpeed(ShuttleFlightLimits.DirectionTier(shuttle, Vector2.UnitX), 100f), Is.EqualTo(45 + 30f / 11).Within(0.001));
        Assert.That(ShuttleFlightLimits.DirectionTier(shuttle, Vector2.UnitY), Is.EqualTo(1));
        Assert.That(ShuttleFlightLimits.GridMaxSpeed(shuttle, 100f), Is.LessThan(55));
    }

    [Test]
    public void ZeroSpeedSettingAndBrakingRemainFinite()
    {
        Assert.That(ShuttleFlightLimits.LimitThrust(Vector2.Zero, Vector2.One, 100, 1f / 60, 0), Is.EqualTo(Vector2.Zero));
        var braking = new Vector2(-100, 0);
        Assert.That(ShuttleFlightLimits.LimitThrust(new Vector2(10, 0), braking, 100, 1f / 60, 70), Is.EqualTo(braking));
    }
}

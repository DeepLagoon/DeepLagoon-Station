using System.Numerics;
using System.Threading.Tasks;
using Content.Shared.Movement.Systems;
using NUnit.Framework;

namespace Content.Tests._DeepLagoon;

[TestFixture]
public sealed class PortedMovementTests
{
    [Test]
    public void FrictionStopsWithoutReversingVelocity()
    {
        var velocity = MovementVelocityMath.Calculate(new Vector2(4, -3), Vector2.Zero, 0.01f, 1, 10, 0, false);
        Assert.That(velocity, Is.EqualTo(Vector2.Zero));
    }

    [Test]
    public void AccelerationRespectsWishSpeedAfterFriction()
    {
        var velocity = MovementVelocityMath.Calculate(new Vector2(2, 0), new Vector2(4, 0), 0.01f, 0.5f, 1, 10, true);
        Assert.That(velocity, Is.EqualTo(new Vector2(4, 0)));
        var drifting = MovementVelocityMath.Calculate(new Vector2(2, 0), new Vector2(4, 0), 0.01f, 0.5f, 1, 10, false);
        Assert.That(drifting, Is.EqualTo(new Vector2(1, 0)));
    }

    [Test]
    public void ParallelEvaluationMatchesSequentialResults()
    {
        const int count = 4096;
        var expected = new Vector2[count];
        var actual = new Vector2[count];
        for (var i = 0; i < count; i++)
            expected[i] = Calculate(i);
        Parallel.For(0, count, i => actual[i] = Calculate(i));
        Assert.That(actual, Is.EqualTo(expected));
    }

    private static Vector2 Calculate(int i) => MovementVelocityMath.Calculate(
        new Vector2(i % 13 - 6, i % 11 - 5), new Vector2(i % 7 - 3, i % 5 - 2),
        0.01f, (i % 4 + 1) / 60f, i % 8, i % 12, i % 3 != 0);
}

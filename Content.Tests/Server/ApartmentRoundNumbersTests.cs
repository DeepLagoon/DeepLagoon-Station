using System;
using System.Linq;
using Content.Server._DeepLagoon.Apartments;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class ApartmentRoundNumbersTests
{
    [Test]
    public void NumbersAreUniqueStableWithinRoundAndInvalidatedOnRestart()
    {
        var registry = new ApartmentRoundNumbers();
        var owners = Enumerable.Range(0, 10000).Select(_ => Guid.NewGuid()).ToArray();
        var numbers = owners.Select(registry.Number).ToArray();
        Assert.That(numbers.Distinct().Count(), Is.EqualTo(owners.Length));
        for (var i = 0; i < owners.Length; i++)
        {
            Assert.That(numbers[i], Is.InRange(10_000_000, 99_999_999));
            Assert.That(registry.Number(owners[i]), Is.EqualTo(numbers[i]));
            Assert.That(registry.Owner(numbers[i]), Is.EqualTo(owners[i]));
        }
        registry.Reset();
        foreach (var number in numbers) Assert.That(registry.Owner(number), Is.Null);
        var next = owners.Select(registry.Number).ToArray();
        Assert.That(next.Intersect(numbers), Is.Empty);
        Assert.That(registry.Owner(1), Is.Null);
    }
}

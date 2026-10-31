using Content.Server.GameTicking;
using NUnit.Framework;

namespace Content.Tests._DeepLagoon;

[TestFixture]
public sealed class RoundNumberTests
{
    [TestCase(0, 0, 0)]
    [TestCase(123, 0, 123)]
    [TestCase(124, 123, 1)]
    [TestCase(130, 123, 7)]
    [TestCase(0, 123, 0)]
    [TestCase(1, -5, 1)]
    public void ResetOnlyChangesThePublicNumber(int databaseId, int offset, int expected)
    {
        Assert.That(RoundNumberCVars.Display(databaseId, offset), Is.EqualTo(expected));
    }
}

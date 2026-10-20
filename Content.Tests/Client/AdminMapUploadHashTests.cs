using System;
using System.Security.Cryptography;
using Content.Client._DeepLagoon.Admin;
using NUnit.Framework;

namespace Content.Tests.Client;

[TestFixture]
public sealed class AdminMapUploadHashTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(55)]
    [TestCase(56)]
    [TestCase(63)]
    [TestCase(64)]
    [TestCase(65)]
    [TestCase(119)]
    [TestCase(120)]
    [TestCase(127)]
    [TestCase(128)]
    [TestCase(129)]
    [TestCase(65536)]
    [TestCase(1000000)]
    public void MatchesServerSha256IncludingPaddingBoundaries(int length)
    {
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        Assert.That(AdminMapUploadHash.Compute(bytes), Is.EqualTo(Convert.ToHexString(SHA256.HashData(bytes))));
    }
}

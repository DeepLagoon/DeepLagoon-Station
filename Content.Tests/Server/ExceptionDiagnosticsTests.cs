using System;
using Content.Server._DeepLagoon.Diagnostics;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class ExceptionDiagnosticsTests
{
    [Test]
    public void IncludesInnerStacksWithoutMessagesOrData()
    {
        Exception error;
        try
        {
            try { throw new ArgumentException("secret-inner-token"); }
            catch (Exception inner) { throw new InvalidOperationException("secret-outer-token", inner); }
        }
        catch (Exception caught) { error = caught; }
        error.Data["credential"] = "secret-data-token";
        var description = ExceptionDiagnostics.Describe(error);
        Assert.That(description, Does.Contain(nameof(ArgumentException)).And.Contain(nameof(InvalidOperationException)));
        Assert.That(description, Does.Contain(nameof(IncludesInnerStacksWithoutMessagesOrData)));
        Assert.That(description, Does.Not.Contain("secret-").And.Not.Contain("credential"));
    }

    [Test]
    public void RepeatedSiteKeepsTheTypeAndLimitsStackOutput()
    {
        string Describe() => ExceptionDiagnostics.Describe(new InvalidOperationException("secret"));
        Assert.That(Describe(), Does.Contain("stack unavailable"));
        Assert.That(Describe(), Does.Contain(nameof(InvalidOperationException)).And.Contain("suppressed"));
    }
}

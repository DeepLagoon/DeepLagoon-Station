using System;
using Content.Client._DeepLagoon.WebUI;
using NUnit.Framework;

namespace Content.Tests._DeepLagoon;

[TestFixture]
public sealed class WebViewWarmCacheTests
{
    [Test]
    public void OnlyOneIdleBrowserSurvivesEvenAfterManyDifferentWindows()
    {
        var cache = new WebViewWarmCache();
        var closed = 0;
        for (var i = 0; i < 1000; i++)
        {
            cache.Retain(new object(), TimeSpan.FromSeconds(i), () => closed++);
            Assert.That(closed, Is.EqualTo(i));
        }
        Assert.That(WebViewWarmCache.Capacity, Is.EqualTo(1));
        cache.Clear();
        Assert.That(closed, Is.EqualTo(1000));
        Assert.That(cache.ExpiresAt, Is.Null);
    }

    [Test]
    public void IdleLeaseExpiresAtThirtySecondsWithoutAnyWindowFrames()
    {
        var cache = new WebViewWarmCache();
        var closed = 0;
        cache.Retain(new object(), TimeSpan.FromSeconds(100), () => closed++);
        cache.Expire(TimeSpan.FromSeconds(129.999));
        Assert.That(closed, Is.Zero);
        cache.Expire(TimeSpan.FromSeconds(130));
        cache.Expire(TimeSpan.FromSeconds(200));
        Assert.That(closed, Is.EqualTo(1));
        Assert.That(cache.ExpiresAt, Is.Null);
    }

    [Test]
    public void DuplicateHideAndExitNotificationsDoNotExtendIdleLease()
    {
        var cache = new WebViewWarmCache();
        var owner = new object();
        var closed = 0;
        cache.Retain(owner, TimeSpan.Zero, () => closed++);
        cache.Retain(owner, TimeSpan.FromSeconds(29), () => Assert.Fail("Duplicate lease replaced the original"));
        cache.Expire(TimeSpan.FromSeconds(30));
        Assert.That(closed, Is.EqualTo(1));
    }

    [Test]
    public void ResumedBrowserIsNotEvictedAndGetsFreshLeaseOnNextHide()
    {
        var cache = new WebViewWarmCache();
        var owner = new object();
        var closed = 0;
        cache.Retain(owner, TimeSpan.Zero, () => closed++);
        cache.Remove(owner);
        cache.Expire(TimeSpan.FromSeconds(30));
        Assert.That(closed, Is.Zero);
        cache.Retain(owner, TimeSpan.FromSeconds(40), () => closed++);
        cache.Expire(TimeSpan.FromSeconds(69));
        Assert.That(closed, Is.Zero);
        cache.Expire(TimeSpan.FromSeconds(70));
        Assert.That(closed, Is.EqualTo(1));
    }

    [Test]
    public void ClosingOldOwnerCannotRemoveItsReplacement()
    {
        var cache = new WebViewWarmCache();
        var old = new object();
        var closed = 0;
        cache.Retain(old, TimeSpan.Zero, () => cache.Remove(old));
        cache.Retain(new object(), TimeSpan.FromSeconds(20), () => closed++);
        cache.Remove(old);
        cache.Expire(TimeSpan.FromSeconds(30));
        Assert.That(closed, Is.Zero);
        cache.Expire(TimeSpan.FromSeconds(50));
        Assert.That(closed, Is.EqualTo(1));
    }

    [Test]
    public void ClientsHaveIndependentCaches()
    {
        var first = new WebViewWarmCache();
        var second = new WebViewWarmCache();
        var closed = false;
        first.Retain(new object(), TimeSpan.Zero, () => closed = true);
        second.Retain(new object(), TimeSpan.Zero, () => Assert.Fail("Other client was evicted"));
        first.Clear();
        Assert.That(closed, Is.True);
        Assert.That(second.ExpiresAt, Is.EqualTo(WebViewWarmCache.IdleLifetime));
    }
}

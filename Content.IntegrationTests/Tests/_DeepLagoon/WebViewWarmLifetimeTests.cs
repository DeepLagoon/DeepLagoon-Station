using System.Reflection;
using Content.Client._DeepLagoon.WebUI;
using Robust.Client.UserInterface;
using Robust.Client.WebView;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class WebViewWarmLifetimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task ShortHidePreservesControlAndReadyStateButExpiryReleasesIt()
    {
        if (!WebViewCapabilities.NativeLifecycle)
            Assert.Ignore("The official module uses the separately tested close/recreate fallback.");
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            using var web = new GameWebView(suspendWhenHidden: true);
            ui.WindowRoot.AddChild(web);
            var frame = typeof(GameWebView).GetMethod("FrameUpdate", Private)!;
            frame.Invoke(web, new object[] { new FrameEventArgs(0) });
            var view = (WebViewControl)typeof(GameWebView).GetField("_view", Private)!.GetValue(web)!;
            Assert.That(view.AlwaysActive, Is.True);
            // Headless never executes the document: simulate only its Ready handshake.
            typeof(GameWebView).GetField("_ready", Private)!.SetValue(web, true);
            web.Visible = false;
            web.Send("update", "{\"data\":{\"name\":\"latest\"}}");
            Assert.That(view.Parent, Is.SameAs(web));
            Assert.That(web.IsReady, Is.True);
            web.Visible = true;
            frame.Invoke(web, new object[] { new FrameEventArgs(0) });
            Assert.That(typeof(GameWebView).GetField("_warmSuspended", Private)!.GetValue(web), Is.False);
            Assert.That(typeof(GameWebView).GetField("_view", Private)!.GetValue(web), Is.SameAs(view));
            web.Orphan();
            Assert.That(web.IsReady, Is.True, "Removing a retained editor from the UI tree must preserve its document");
            ui.WindowRoot.AddChild(web);
            frame.Invoke(web, new object[] { new FrameEventArgs(0) });
            Assert.That(typeof(GameWebView).GetField("_warmSuspended", Private)!.GetValue(web), Is.False);
            web.Visible = false;
            pair.Client.ResolveDependency<WebViewLifetimeRegistry>().Warm.Expire(TimeSpan.MaxValue);
            Assert.That(web.IsReady, Is.False);
            Assert.That(view.Parent, Is.Null);
            Assert.That(view.AlwaysActive, Is.False);
            web.Visible = true;
            frame.Invoke(web, new object[] { new FrameEventArgs(0) });
            var replacement = (WebViewControl)typeof(GameWebView).GetField("_view", Private)!.GetValue(web)!;
            Assert.That(replacement, Is.Not.SameAs(view));
            Assert.That(pair.Client.ResolveDependency<WebViewLifetimeRegistry>().Active.ContainsKey(replacement), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StockModuleHidingClosesAndRecreatesBrowserWithoutForkApi()
    {
        Assert.That(WebViewCapabilities.NativeLifecycle, Is.False, "Production tests must use the stock client module");
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true, Destructive = true });
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            using var web = new GameWebView(suspendWhenHidden: true);
            ui.WindowRoot.AddChild(web);
            var frame = typeof(GameWebView).GetMethod("FrameUpdate", Private)!;
            frame.Invoke(web, new object[] { new FrameEventArgs(0) });
            var field = typeof(GameWebView).GetField("_view", Private)!;
            var first = (WebViewControl)field.GetValue(web)!;
            Assert.That(first.AlwaysActive, Is.False);
            typeof(GameWebView).GetField("_ready", Private)!.SetValue(web, true);
            web.Visible = false;
            Assert.That(first.Parent, Is.Null);
            Assert.That(web.IsReady, Is.False);
            Assert.That(field.GetValue(web), Is.Not.SameAs(first));
            web.Visible = true;
            frame.Invoke(web, new object[] { new FrameEventArgs(0) });
            Assert.That(web.BrowserActive, Is.True);
            // Mirrors the official engine's mod-loader -> CEF -> entities order.
            using var hidden = new GameWebView(suspendWhenHidden: true);
            ui.WindowRoot.AddChild(hidden);
            frame.Invoke(hidden, new object[] { new FrameEventArgs(0) });
            hidden.Visible = false;
            WebViewLifetime.Shutdown();
            Assert.That(web.BrowserActive, Is.False);
            Assert.That(web.IsReady, Is.False);
            frame.Invoke(web, new object[] { new FrameEventArgs(0) });
            Assert.That(web.BrowserActive, Is.False, "Shutdown cannot recreate a browser");
            hidden.Visible = true;
            frame.Invoke(hidden, new object[] { new FrameEventArgs(0) });
            Assert.That(hidden.BrowserActive, Is.False, "An unregistered idle host cannot restart after shutdown");
            Assert.DoesNotThrow(() => web.Orphan());
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StockModuleRepeatedHideAndDetachDoNotRegisterUnusedReplacement(bool suspendWhenHidden)
    {
        if (WebViewCapabilities.NativeLifecycle) Assert.Ignore("Stock close/recreate regression");
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var registry = pair.Client.ResolveDependency<WebViewLifetimeRegistry>();
            var baseline = registry.Active.Count;
            var field = typeof(GameWebView).GetField("_view", Private)!;
            var frame = typeof(GameWebView).GetMethod("FrameUpdate", Private)!;
            using var web = new GameWebView(suspendWhenHidden: suspendWhenHidden);
            ui.WindowRoot.AddChild(web);
            for (var i = 0; i < 20; i++)
            {
                web.Visible = true;
                frame.Invoke(web, new object[] { new FrameEventArgs(0) });
                Assert.That(registry.Active.Count, Is.EqualTo(baseline + 1));
                var view = (WebViewControl)field.GetValue(web)!;
                web.Visible = false;
                Assert.That(field.GetValue(web), Is.Null, "Hidden windows must not allocate a replacement");
                Assert.That(view.Parent, Is.Null);
                Assert.That(registry.Active.Count, Is.EqualTo(baseline));
                web.Visible = true;
                frame.Invoke(web, new object[] { new FrameEventArgs(0) });
                web.Orphan();
                Assert.That(field.GetValue(web), Is.Null);
                Assert.That(registry.Active.Count, Is.EqualTo(baseline));
                ui.WindowRoot.AddChild(web);
            }
            web.Orphan();
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SharedIdleTimerClosesDetachedBrowserWithoutOwnerFrames()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        WebViewControl retained = null;
        var closed = false;
        await pair.Client.WaitAssertion(() =>
        {
            var registry = pair.Client.ResolveDependency<WebViewLifetimeRegistry>();
            var timing = pair.Client.ResolveDependency<IGameTiming>();
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            retained = new WebViewControl { AlwaysActive = true };
            WebViewLifetime.Register(retained, () => { });
            // Exercise the real timer with an almost expired lease, without a 30-second sleep.
            registry.Warm.Retain(retained, timing.RealTime - WebViewWarmCache.IdleLifetime + TimeSpan.FromMilliseconds(1), () =>
            {
                WebViewLifetime.Close(retained);
                closed = true;
            });
            registry.ScheduleWarmClose(timing, ui);
            Assert.That(retained.Parent, Is.Null);
        });
        await pair.Client.WaitRunTicks(5);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(closed, Is.True, "Expiry must not depend on a retained control receiving FrameUpdate");
            Assert.That(retained.AlwaysActive, Is.False);
            var registry = pair.Client.ResolveDependency<WebViewLifetimeRegistry>();
            Assert.That(registry.Active.ContainsKey(retained), Is.False);
            Assert.That(registry.Warm.ExpiresAt, Is.Null);
        });
        await pair.CleanReturnAsync();
    }
}

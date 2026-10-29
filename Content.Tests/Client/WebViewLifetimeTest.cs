using System;
using System.Collections.Generic;
using System.Reflection;
using Content.Client._DeepLagoon.WebUI;
using NUnit.Framework;
using Robust.Client.UserInterface;
using Robust.Client.WebView;
using Robust.Shared.IoC;
using Robust.UnitTesting;

namespace Content.Tests.Client;

[TestFixture]
public sealed partial class WebViewLifetimeTest : RobustUnitTest
{
    public override UnitTestProject Project => UnitTestProject.Client;
    private readonly List<BrowserProxy> _browsers = new();
    private Action<BrowserProxy> _configureBrowser;

    protected override void OverrideIoC()
    {
        base.OverrideIoC();
        var assembly = typeof(WebViewControl).Assembly;
        var managerType = assembly.GetType("Robust.Client.WebView.IWebViewManagerInternal", true)!;
        var browserType = assembly.GetType("Robust.Client.WebView.IWebViewControlImpl", true)!;
        var manager = (ManagerProxy) DispatchProxy.Create(managerType, typeof(ManagerProxy));
        manager.CreateBrowser = () =>
        {
            var browser = (BrowserProxy) DispatchProxy.Create(browserType, typeof(BrowserProxy));
            _browsers.Add(browser);
            _configureBrowser?.Invoke(browser);
            return browser;
        };
        IoCManager.Instance!.RegisterInstance(managerType, manager);
        IoCManager.Instance.RegisterInstance(typeof(WebViewLifetimeRegistry), new WebViewLifetimeRegistry());
    }

    [OneTimeSetUp]
    public void InitializeUi() => IoCManager.Resolve<IUserInterfaceManager>().InitializeTesting();

    [TestCase(false)]
    [TestCase(true)]
    public void ClosingAncestorDeliversNativeBrowserExit(bool nested)
    {
        var ui = IoCManager.Resolve<IUserInterfaceManager>();
        var registry = IoCManager.Resolve<WebViewLifetimeRegistry>();
        var baseline = registry.Active.Count;
        var start = _browsers.Count;
        for (var i = 0; i < 40; i++)
        {
            var host = new ExitHost();
            var outer = nested ? new Control() : host;
            if (nested) outer.AddChild(host);
            ui.WindowRoot.AddChild(outer);
            var view = new WebViewControl();
            host.View = view;
            host.AddChild(view);
            WebViewLifetime.Register(view, () => { });
            Assert.That(_browsers[^1].Open, Is.True);
            outer.Orphan();
            Assert.That(view.IsInsideTree, Is.False, "Removing the child inside ancestor ExitedTree must not skip its exit notification");
            Assert.That(_browsers[^1].Open, Is.False, "Native CloseBrowser must run, not just removal from the content registry");
            DrainDeferred(ui);
            Assert.That(view.Parent, Is.Null);
            Assert.That(registry.Active.Count, Is.EqualTo(baseline));
        }
        Assert.That(_browsers.GetRange(start, 40), Is.All.Matches<BrowserProxy>(browser => browser.Starts == 1 && browser.Closes == 1));
    }

    private static void DrainDeferred(IUserInterfaceManager ui)
    {
        var queue = (Queue<Action>) ui.GetType().GetField("_deferQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ui)!;
        while (queue.TryDequeue(out var action)) action();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DisposingBeforeDeferredDetachClosesExactlyOnce(bool disposeParent)
    {
        var ui = IoCManager.Resolve<IUserInterfaceManager>();
        var registry = IoCManager.Resolve<WebViewLifetimeRegistry>();
        var baseline = registry.Active.Count;
        var host = new ExitHost();
        ui.WindowRoot.AddChild(host);
        var view = new WebViewControl();
        host.View = view;
        host.AddChild(view);
        WebViewLifetime.Register(view, () => { });

        host.Orphan();
#pragma warning disable CS0618 // Exercise callers disposing controls before the UI deferred queue runs.
        if (disposeParent) host.Dispose(); else view.Dispose();
#pragma warning restore CS0618
        DrainDeferred(ui);
        WebViewLifetime.Close(view);
        DrainDeferred(ui);

        Assert.That(view.Disposed, Is.True);
        Assert.That(view.Parent, Is.Null);
        Assert.That(view.IsInsideTree, Is.False);
        Assert.That(registry.Active.Count, Is.EqualTo(baseline));
        Assert.That(_browsers[^1].Closes, Is.EqualTo(1));
        Assert.That(_browsers[^1].Open, Is.False);
    }

    [Test]
    public void ClosingVisibleChildAndClosingAgainClosesExactlyOnce()
    {
        var ui = IoCManager.Resolve<IUserInterfaceManager>();
        var host = new Control();
        ui.WindowRoot.AddChild(host);
        var view = new WebViewControl();
        host.AddChild(view);
        WebViewLifetime.Register(view, () => { });
        WebViewLifetime.Close(view);
        WebViewLifetime.Close(view);
        DrainDeferred(ui);
        Assert.That(view.Parent, Is.Null);
        Assert.That(view.IsInsideTree, Is.False);
        Assert.That(_browsers[^1].Closes, Is.EqualTo(1));
        Assert.That(_browsers[^1].Open, Is.False);
        host.Orphan();
    }

    private sealed class ExitHost : Control
    {
        public WebViewControl View = default!;
        public Action<WebViewControl> Close;
        protected override void ExitedTree()
        {
            base.ExitedTree();
            if (Close != null) Close(View); else WebViewLifetime.Close(View);
        }
    }

    public class ManagerProxy : DispatchProxy
    {
        public Func<object> CreateBrowser = default!;
        protected override object Invoke(MethodInfo method, object[] args) => method.Name == "MakeControlImpl" ? CreateBrowser() : null;
    }

    public class BrowserProxy : DispatchProxy
    {
        public bool Open;
        public int Starts, Closes;
        public Action StartNative, CloseNative;
        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == "get_IsOpen") return Open;
            if (method.Name == "StartBrowser") { Open = true; Starts++; StartNative?.Invoke(); }
            if (method.Name == "CloseBrowser") { Assert.That(Open, Is.True, "A browser must not be closed twice"); Open = false; Closes++; CloseNative?.Invoke(); }
            return method.ReturnType == typeof(bool) ? false : null;
        }
    }
}

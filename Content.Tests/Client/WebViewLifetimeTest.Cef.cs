using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Content.Client._DeepLagoon.WebUI;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using Robust.Client.UserInterface;
using Robust.Client.WebView;
using Robust.Shared.IoC;
using Xilium.CefGlue;

namespace Content.Tests.Client;

public sealed partial class WebViewLifetimeTest
{
    [Test]
    public void NativeCefProcessesRemainBoundedAfterAncestorExit()
    {
        var module = Environment.GetEnvironmentVariable("DEEPLAGOON_CEF_MODULE");
        var output = Environment.GetEnvironmentVariable("DEEPLAGOON_CEF_REPORT");
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(module) || string.IsNullOrEmpty(output))
            Assert.Ignore("Opt-in Windows native CEF regression: set DEEPLAGOON_CEF_MODULE and DEEPLAGOON_CEF_REPORT.");
        Directory.CreateDirectory(output);
        Assert.That(File.Exists(Path.Combine(module, "Robust.Client.WebView.exe")), Is.True);
        using var process = Process.GetCurrentProcess();
        Environment.SetEnvironmentVariable("ROBUST_CEF_BROWSER_PROCESS_ID", process.Id.ToString());
        Environment.SetEnvironmentVariable("ROBUST_CEF_BROWSER_PROCESS_MODULE", process.MainModule!.FileName);
        var library = NativeLibrary.Load(Path.Combine(module, "libcef.dll"));
        NativeLibrary.SetDllImportResolver(typeof(CefRuntime).Assembly, (name, _, _) =>
            name is "libcef" or "libcef.dll" ? library : IntPtr.Zero);
        CefRuntime.Load(module);
        var settings = new CefSettings
        {
            WindowlessRenderingEnabled = true, ExternalMessagePump = true, NoSandbox = true,
            BrowserSubprocessPath = Path.Combine(module, "Robust.Client.WebView.exe"),
            LocalesDirPath = Path.Combine(module, "cef_resources", "locales"),
            ResourcesDirPath = Path.Combine(module, "cef_resources"),
            CachePath = Path.Combine(output, "cef-cache-" + process.Id),
            LogFile = Path.Combine(output, "cef-" + process.Id + ".log"),
        };
        var app = (CefApp) Activator.CreateInstance(typeof(WebViewControl).Assembly.GetType("Robust.Client.WebView.Cef.RobustCefApp", true)!, new object[] { null });
        CefRuntime.Initialize(new CefMainArgs(new[] { "lifetime-smoke", "--disable-in-process-stack-traces" }), settings, app);
        var live = new HashSet<int>();
        var clients = new List<SmokeClient>();
        var native = new List<CefBrowser>();
        var report = new List<object>();
        var ui = IoCManager.Resolve<IUserInterfaceManager>();
        var registry = IoCManager.Resolve<WebViewLifetimeRegistry>();
        void Pump(int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            do { CefRuntime.DoMessageLoopWork(); Thread.Sleep(5); } while (watch.ElapsedMilliseconds < milliseconds);
        }
        void CloseAll()
        {
            foreach (var browser in native) if (live.Contains(browser.Identifier)) browser.GetHost().CloseBrowser(true);
            var watch = Stopwatch.StartNew();
            while (live.Count > 0 && watch.ElapsedMilliseconds < 10000) Pump(25);
            Assert.That(live, Is.Empty);
            foreach (var browser in native) browser.Dispose();
            native.Clear(); clients.Clear();
            Pump(1000);
        }
        try
        {
            _configureBrowser = proxy =>
            {
                CefBrowser browser = null;
                proxy.StartNative = () =>
                {
                    var client = new SmokeClient(live);
                    var info = CefWindowInfo.Create(); info.SetAsWindowless(IntPtr.Zero, false);
                    var browserSettings = new CefBrowserSettings { WindowlessFrameRate = 1 };
                    browser = CefBrowserHost.CreateBrowserSync(info, client, browserSettings, "data:text/html,<title>lifetime-" + native.Count + "</title>");
                    clients.Add(client); native.Add(browser);
                };
                proxy.CloseNative = () => browser.GetHost().CloseBrowser(true);
            };
            foreach (var baseline in new[] { true, false })
            {
                const int count = 12;
                for (var index = 0; index < count; index++)
                {
                    var host = new ExitHost();
                    if (baseline) host.Close = view =>
                    {
                        registry.Active.Remove(view);
                        view.ReleaseKeyboardFocus(); view.AlwaysActive = false; view.Orphan();
                    };
                    ui.WindowRoot.AddChild(host);
                    var view = new WebViewControl(); host.View = view; host.AddChild(view);
                    WebViewLifetime.Register(view, () => { });
                    Pump(100);
                    host.Orphan();
                    if (!baseline)
                    {
#pragma warning disable CS0618 // Cover disposal before the pending UI detach, with real native browsers.
                        if (index % 3 == 0) host.Dispose();
                        if (index % 3 == 1) view.Dispose();
#pragma warning restore CS0618
                    }
                    DrainDeferred(ui); Pump(200);
                    if (!baseline) Assert.That(_browsers[^1].Open, Is.False);
                }
                Pump(1500);
                var children = CefChildren(process.Id);
                report.Add(new { Baseline = baseline, Windows = count, NativeBrowsers = live.Count, Processes = children.Length, ProcessIds = children });
                Assert.That(live.Count, Is.EqualTo(baseline ? count : 0));
                if (!baseline) Assert.That(children.Length, Is.LessThanOrEqualTo(5), "Only shared GPU/services and a possible spare renderer may remain");
                CloseAll();
            }
            File.WriteAllText(Path.Combine(output, "native-processes.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.Out.WriteLine(JsonSerializer.Serialize(report));
        }
        finally
        {
            _configureBrowser = null;
            CloseAll();
            CefRuntime.Shutdown();
        }
    }

    private sealed class SmokeClient : CefClient
    {
        private readonly SmokeRender _render = new();
        private readonly SmokeLifespan _lifetime;
        public SmokeClient(HashSet<int> live) => _lifetime = new SmokeLifespan(live);
        protected override CefRenderHandler GetRenderHandler() => _render;
        protected override CefLifeSpanHandler GetLifeSpanHandler() => _lifetime;
    }
    private sealed class SmokeRender : CefRenderHandler
    {
        protected override void GetViewRect(CefBrowser browser, out CefRectangle rect) => rect = new CefRectangle(0, 0, 32, 32);
        protected override CefAccessibilityHandler GetAccessibilityHandler() => null;
        protected override bool GetScreenInfo(CefBrowser browser, CefScreenInfo screenInfo) => false;
        protected override void OnPopupSize(CefBrowser browser, CefRectangle rect) { }
        protected override void OnPaint(CefBrowser browser, CefPaintElementType type, CefRectangle[] dirtyRects, IntPtr buffer, int width, int height) { }
        protected override void OnAcceleratedPaint(CefBrowser browser, CefPaintElementType type, CefRectangle[] dirtyRects, in CefAcceleratedPaintInfo info) { }
        protected override void OnScrollOffsetChanged(CefBrowser browser, double x, double y) { }
        protected override void OnImeCompositionRangeChanged(CefBrowser browser, CefRange selectedRange, CefRectangle[] characterBounds) { }
    }
    private sealed class SmokeLifespan(HashSet<int> live) : CefLifeSpanHandler
    {
        protected override void OnAfterCreated(CefBrowser browser) => live.Add(browser.Identifier);
        protected override void OnBeforeClose(CefBrowser browser) => live.Remove(browser.Identifier);
    }

    private static int[] CefChildren(int parent)
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        var entry = new ProcessEntry { Size = (uint) Marshal.SizeOf<ProcessEntry>() };
        var result = new List<int>();
        if (!Process32FirstW(snapshot, ref entry)) return result.ToArray();
        do
        {
            if (entry.Parent == parent && entry.Executable.Equals("Robust.Client.WebView.exe", StringComparison.OrdinalIgnoreCase)) result.Add((int) entry.Id);
        } while (Process32NextW(snapshot, ref entry));
        return result.ToArray();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Id;
        public UIntPtr Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll")] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
}

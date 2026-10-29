using System.Linq;
using Robust.Client.UserInterface;
using Robust.Client.WebView;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>Close through the UI tree lifecycle; CEF owns its buffers until OnBeforeClose.</summary>
public static class WebViewLifetime
{
    // A client-scoped service also isolates parallel integration clients.
    private static Dictionary<WebViewControl, Action> Active => IoCManager.Resolve<WebViewLifetimeRegistry>().Active;
    public static bool IsShuttingDown => IoCManager.Resolve<WebViewLifetimeRegistry>().IsShuttingDown;

    public static void Register(WebViewControl view, Action prepareForShutdown) => Active.Add(view, prepareForShutdown);

    /// <summary>Called by content EntryPoint before the engine's WebView shutdown.</summary>
    public static void Shutdown()
    {
        IoCManager.Resolve<WebViewLifetimeRegistry>().StopWarmTimer();
        foreach (var (view, prepare) in Active.ToArray())
        {
            prepare();
            Close(view);
        }
    }

    public static void Close(WebViewControl view)
    {
        Resume(view);
        Active.Remove(view);
        view.ReleaseKeyboardFocus();
        view.AlwaysActive = false;
        view.Orphan();
    }

    public static void KeepWarm(WebViewControl view, Action close)
    {
        var registry = IoCManager.Resolve<WebViewLifetimeRegistry>();
        if (registry.IsShuttingDown) return;
        var timing = IoCManager.Resolve<IGameTiming>();
        registry.Warm.Retain(view, timing.RealTime, close);
        registry.ScheduleWarmClose(timing, view.UserInterfaceManager);
    }

    public static void Resume(WebViewControl view) => IoCManager.Resolve<WebViewLifetimeRegistry>().Warm.Remove(view);
}

public sealed class WebViewLifetimeRegistry
{
    public readonly Dictionary<WebViewControl, Action> Active = new();
    public readonly WebViewWarmCache Warm = new();
    public bool IsShuttingDown { get; private set; }
    private readonly System.Threading.CancellationTokenSource _warmCancellation = new();
    private bool _warmTimerPending;

    public void ScheduleWarmClose(IGameTiming timing, IUserInterfaceManager ui)
    {
        if (_warmTimerPending || _warmCancellation.IsCancellationRequested || Warm.ExpiresAt is not { } expiry) return;
        _warmTimerPending = true;
        var delay = Math.Max(1, (int)Math.Ceiling((expiry - timing.RealTime).TotalMilliseconds));
        // A single client-owned timer also runs for windows detached from the UI tree.
        Timer.Spawn(delay, () => ui.DeferAction(() =>
        {
            _warmTimerPending = false;
            if (_warmCancellation.IsCancellationRequested) return;
            Warm.Expire(timing.RealTime);
            ScheduleWarmClose(timing, ui);
        }), _warmCancellation.Token);
    }

    public void StopWarmTimer()
    {
        IsShuttingDown = true;
        _warmCancellation.Cancel();
    }
}

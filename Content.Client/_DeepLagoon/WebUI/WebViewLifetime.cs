using System.Linq;
using System.Numerics;
using Robust.Client.UserInterface;
using Robust.Client.WebView;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>Close through the UI tree lifecycle; CEF owns its buffers until OnBeforeClose.</summary>
public static class WebViewLifetime
{
    // A client-scoped service also isolates parallel integration clients.
    private static Dictionary<WebViewControl, Action> Active => IoCManager.Resolve<WebViewLifetimeRegistry>().Active;
    public static bool IsShuttingDown => IoCManager.Resolve<WebViewLifetimeRegistry>().IsShuttingDown;

    /// <summary>
    /// The signed module starts with a 1x1 texture, even when a reused control is
    /// already laid out. Deliver a real resize before its first visible paint.
    /// Only public layout APIs are used; native buffer handling stays in WebView.
    /// </summary>
    public static bool InitializeViewport(WebViewControl view)
    {
        if (view.Disposed || !view.IsInsideTree || view.PixelWidth <= 0 || view.PixelHeight <= 0)
            return false;
        var position = view.Position;
        var size = view.Size;
        view.Arrange(UIBox2.FromDimensions(position, size + Vector2.One));
        view.Arrange(UIBox2.FromDimensions(position, size));
        return true;
    }

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
        if (view.Disposed) return;
        view.ReleaseKeyboardFocus();
        view.AlwaysActive = false;
        // Ancestor exit visits the parent before its children. Removing the view
        // here would skip its ExitedTree and leave the stock CEF browser open.
        if (view.IsInsideTree && view.Parent is { IsInsideTree: false })
        {
            var closing = IoCManager.Resolve<WebViewLifetimeRegistry>().Closing;
            if (!closing.Add(view)) return;
            view.UserInterfaceManager.DeferAction(() =>
            {
                closing.Remove(view);
                // The owner may dispose its children before the deferred queue runs.
                if (!view.Disposed) view.Orphan();
            });
            return;
        }
        IoCManager.Resolve<WebViewLifetimeRegistry>().Closing.Remove(view);
        view.Orphan();
    }

    /// <summary>
    /// Detach children that already received ExitedTree before an owner disposes
    /// its remaining controls. The signed module may still query their viewport
    /// until its asynchronous native close completes.
    /// </summary>
    public static void DetachClosingChildren(Control owner)
    {
        foreach (var view in IoCManager.Resolve<WebViewLifetimeRegistry>().Closing.ToArray())
        {
            if (!view.Disposed && !view.IsInsideTree && view.Parent == owner)
                Close(view);
        }
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
    public readonly HashSet<WebViewControl> Closing = new();
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

using Robust.Client.UserInterface.Controls;
using Robust.Shared.Network;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client._DeepLagoon.Performance;

public sealed class PerformanceCounter : Label
{
    [Dependency] private IClientNetManager _netManager = default!;
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly char[] _textBuffer = new char[128];
    private TimeSpan _nextUpdate;

    public PerformanceCounter()
    {
        IoCManager.InjectDependencies(this);
        FontColorShadowOverride = Color.Black;
        ShadowOffsetXOverride = 1;
        ShadowOffsetYOverride = 1;
        MouseFilter = MouseFilterMode.Ignore;
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        if (!VisibleInTree || _timing.RealTime < _nextUpdate)
            return;

        _nextUpdate = _timing.RealTime + TimeSpan.FromSeconds(0.25);
        var fps = _timing.FramesPerSecondAvg;
        var channel = _netManager.ServerChannel;
        var serverFps = _entities.System<ServerFpsSystem>().FramesPerSecond;
        if (channel == null)
            TextMemory = FormatHelpers.FormatIntoMem(_textBuffer, $"FPS: {fps:N0} | Ping: — | Server FPS: —");
        else if (serverFps is { } value)
            TextMemory = FormatHelpers.FormatIntoMem(_textBuffer, $"FPS: {fps:N0} | Ping: {channel.Ping} ms | Server FPS: {value:N0}");
        else
            TextMemory = FormatHelpers.FormatIntoMem(_textBuffer, $"FPS: {fps:N0} | Ping: {channel.Ping} ms | Server FPS: —");
    }
}

using Content.Shared._DeepLagoon.Voyages;
using Content.Shared.GameTicking;
using Robust.Shared.Timing;
namespace Content.Client._DeepLagoon.Voyages;
public sealed class SectorExpeditionUiSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    public SectorExpeditionWindow? Window { get; private set; }
    private TimeSpan _next;
    public override void Initialize()
    {
        SubscribeNetworkEvent<SectorExpeditionSnapshot>((state, _) => { if (Window?.Console == state.Console) Window.Apply(state); });
        SubscribeNetworkEvent<RoundRestartCleanupEvent>(_ => Close());
    }
    public SectorExpeditionWindow Open(EntityUid console)
    {
        if (Window?.Console != GetNetEntity(console)) Close();
        if (Window == null) { Window = new(GetNetEntity(console)); Window.Request += request => RaiseNetworkEvent(request); }
        if (Window.IsOpen) Window.MoveToFront(); else Window.OpenCentered(); _next = TimeSpan.Zero; return Window;
    }
    public void Close() { Window?.Close(); Window?.Dispose(); Window = null; }
    public override void FrameUpdate(float frameTime)
    {
        if (Window is not { IsOpen: true } || _timing.RealTime < _next) return;
        _next = _timing.RealTime + TimeSpan.FromSeconds(2); RaiseNetworkEvent(Window.RefreshRequest());
    }
    public override void Shutdown() { Close(); base.Shutdown(); }
}

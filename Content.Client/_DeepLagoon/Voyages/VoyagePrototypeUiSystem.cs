using Content.Shared.GameTicking;
using Content.Shared._DeepLagoon.Voyages;
using Robust.Shared.Timing;
namespace Content.Client._DeepLagoon.Voyages;
public sealed class VoyagePrototypeUiSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    public VoyagePrototypeWindow? Window { get; private set; }
    private TimeSpan _next;
    public override void Initialize() { SubscribeNetworkEvent<VoyageSnapshot>(OnSnapshot); SubscribeNetworkEvent<RoundRestartCleanupEvent>(_ => { Window?.Close(); Window?.Dispose(); Window = null; }); }
    public void Open(EntityUid? console = null)
    {
        var net = console == null ? (NetEntity?)null : GetNetEntity(console.Value);
        if (Window != null && Window.Console != net) { Window.Close(); Window.Dispose(); Window = null; }
        if (Window == null)
        {
            Window = new VoyagePrototypeWindow(net);
            Window.Request += request => { _next = _timing.RealTime + TimeSpan.FromSeconds(2); RaiseNetworkEvent(request); };
        }
        if (Window.IsOpen) Window.MoveToFront(); else Window.OpenCentered();
        _next = TimeSpan.Zero;
    }
    public override void FrameUpdate(float frameTime)
    {
        if (Window is not { IsOpen: true, Console: { } console } || _timing.RealTime < _next) return;
        _next = _timing.RealTime + TimeSpan.FromSeconds(2);
        RaiseNetworkEvent(Window.RefreshRequest());
    }
    private void OnSnapshot(VoyageSnapshot snapshot, EntitySessionEventArgs args)
    {
        if (Window?.Console == snapshot.Console) Window.Apply(snapshot);
    }
    public override void Shutdown() { Window?.Close(); Window?.Dispose(); Window = null; base.Shutdown(); }
}

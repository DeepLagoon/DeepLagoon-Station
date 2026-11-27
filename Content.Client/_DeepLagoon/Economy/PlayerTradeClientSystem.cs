using System.Linq;
using Content.Shared._DeepLagoon.Economy;

namespace Content.Client._DeepLagoon.Economy;

public sealed class PlayerTradeClientSystem : EntitySystem
{
    private readonly Dictionary<EntityUid, TradeWindow> _windows = new();
    public override void Initialize() { UpdatesOutsidePrediction = true; SubscribeNetworkEvent<TradeSnapshot>(OnSnapshot); }
    public TradeWindow Open(EntityUid console, Action<BoundUserInterfaceMessage>? localMarket = null)
    {
        if (_windows.TryGetValue(console, out var previous)) previous.Dispose();
        var window = new TradeWindow(request => { request.Console = GetNetEntity(console); RaiseNetworkEvent(request); });
        if (localMarket != null) window.ConfigureLocalMarket(localMarket);
        _windows[console] = window;
        window.OnClose += () => { if (_windows.GetValueOrDefault(console) == window) _windows.Remove(console); window.Dispose(); };
        window.OpenCentered();
        window.Refresh();
        return window;
    }
    public Robust.Client.UserInterface.Control OpenEmbedded(EntityUid pda)
    {
        Close(pda);
        var window = new TradeWindow(request => { request.Console = GetNetEntity(pda); RaiseNetworkEvent(request); }, true);
        _windows[pda] = window;
        var root = window.Embed();
        window.Refresh();
        return root;
    }
    public override void Update(float frameTime)
    {
        foreach (var (uid, window) in _windows.ToArray())
        {
            if (window.EmbeddedDisposed) Close(uid);
            else if (window.IsEmbedded) window.Poll();
        }
    }
    public void Close(EntityUid console)
    {
        if (_windows.Remove(console, out var window)) { window.DisposeEmbedded(); window.Dispose(); }
    }
    private void OnSnapshot(TradeSnapshot snapshot)
    {
        if (TryGetEntity(snapshot.Console, out var console) && console is { } uid && _windows.TryGetValue(uid, out var window)) window.Update(snapshot);
    }
}

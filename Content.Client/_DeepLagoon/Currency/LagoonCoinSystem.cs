using Content.Shared._DeepLagoon.Currency;
using Robust.Shared.Network;
using Robust.Client.Graphics;

namespace Content.Client._DeepLagoon.Currency;

public sealed partial class LagoonCoinSystem : EntitySystem
{
    [Dependency] private INetManager _net = default!;
    [Dependency] private IOverlayManager _overlays = default!;
    private LagoonCoinRewardOverlay _overlay = default!;
    public long Balance { get; private set; } = -1;
    public event Action<long>? BalanceUpdated;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<LagoonCoinBalanceEvent>(ev => SetBalance(ev.Balance));
        _overlay = new LagoonCoinRewardOverlay();
        _overlays.AddOverlay(_overlay);
        SubscribeNetworkEvent<LagoonCoinRewardEvent>(ev => _overlay.Enqueue(ev));
        _net.Disconnect += OnDisconnected;
    }

    public override void Shutdown()
    {
        _net.Disconnect -= OnDisconnected;
        _overlays.RemoveOverlay(_overlay);
        base.Shutdown();
    }

    private void OnDisconnected(object? sender, NetDisconnectedArgs args)
    {
        _overlay.Clear();
        SetBalance(-1);
    }
    private void SetBalance(long balance)
    {
        Balance = balance;
        BalanceUpdated?.Invoke(balance);
    }
    public void RequestBalance() => RaiseNetworkEvent(new LagoonCoinBalanceRequest());
}

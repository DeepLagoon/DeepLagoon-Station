using Content.Shared._DeepLagoon.Currency;
using Robust.Shared.Network;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._DeepLagoon.Currency;

public sealed partial class LagoonCoinSystem : EntitySystem
{
    [Dependency] private INetManager _net = default!;
    [Dependency] private IUserInterfaceManager _ui = default!;
    private LagoonCoinRewardNotification _notification = default!;
    public long Balance { get; private set; } = -1;
    public event Action<long>? BalanceUpdated;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<LagoonCoinBalanceEvent>(ev => SetBalance(ev.Balance));
        _notification = new LagoonCoinRewardNotification();
        _ui.PopupRoot.AddChild(_notification);
        LayoutContainer.SetAnchorAndMarginPreset(_notification, LayoutContainer.LayoutPreset.CenterTop, margin: 96);
        LayoutContainer.SetGrowHorizontal(_notification, LayoutContainer.GrowDirection.Both);
        SubscribeNetworkEvent<LagoonCoinRewardEvent>(ev => _notification.Enqueue(ev));
        _net.Disconnect += OnDisconnected;
    }

    public override void Shutdown()
    {
        _net.Disconnect -= OnDisconnected;
        _notification.Dispose();
        base.Shutdown();
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);
        _notification.Update();
    }

    private void OnDisconnected(object? sender, NetDisconnectedArgs args)
    {
        _notification.Clear();
        SetBalance(-1);
    }
    private void SetBalance(long balance)
    {
        Balance = balance;
        BalanceUpdated?.Invoke(balance);
    }
    public void RequestBalance() => RaiseNetworkEvent(new LagoonCoinBalanceRequest());
}

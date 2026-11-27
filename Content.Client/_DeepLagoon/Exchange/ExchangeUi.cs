using Content.Client.UserInterface.Fragments;
using Content.Shared._DeepLagoon.Exchange;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Exchange;

public sealed partial class ExchangeUi : UIFragment
{
    private ExchangeView? _view;
    private ScrollContainer? _scroll;
    private EntityUid _owner;
    public override Control GetUIFragmentRoot() => _scroll!;
    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        // Cartridge state updates call Setup repeatedly while the existing fragment stays attached.
        // Keep its subscriptions and visible view instead of redirecting snapshots to a hidden replacement.
        if (_scroll is { Disposed: false } && _owner == userInterface.Owner)
            return;
        _owner = userInterface.Owner;
        _view = new ExchangeView();
        _scroll = new ScrollContainer { HorizontalExpand = true, VerticalExpand = true };
        _scroll.AddChild(_view);
        IoCManager.Resolve<IEntityManager>().System<ExchangeClientSystem>().Open(userInterface.Owner, _view);
    }
    public override void UpdateState(BoundUserInterfaceState state) { }
}

public sealed partial class ExchangeClientSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    private ExchangeView? _view;
    private EntityUid _pda;
    private TimeSpan _nextRefresh;

    public override void Initialize()
    {
        UpdatesOutsidePrediction = true;
        SubscribeNetworkEvent<ExchangeSnapshot>(snapshot =>
        {
            if (_view == null || snapshot.Pda != GetNetEntity(_pda)) return;
            if (_view.Apply(snapshot)) _nextRefresh = _timing.RealTime + TimeSpan.FromSeconds(10);
        });
    }
    public void Open(EntityUid pda, ExchangeView view)
    {
        _pda = pda;
        _view = view;
        view.Request += (action, asset, units, quote, period, recipient, password, newPassword) =>
        {
            if (_view != view || !view.VisibleInTree) return;
            RaiseNetworkEvent(new ExchangeRequest(GetNetEntity(pda), action, asset, units, quote, period, recipient, password, newPassword));
            _nextRefresh = _timing.RealTime + TimeSpan.FromSeconds(action == ExchangeAction.Refresh ? 1 : 10);
        };
        _nextRefresh = TimeSpan.Zero;
    }
    public override void Update(float frameTime)
    {
        if (_view == null || !_view.IsInsideTree || !_view.VisibleInTree || _timing.RealTime < _nextRefresh) return;
        _nextRefresh = _timing.RealTime + TimeSpan.FromSeconds(10);
        _view.Refresh();
    }
}

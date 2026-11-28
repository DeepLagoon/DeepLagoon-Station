using Content.Client.UserInterface.Fragments;
using Robust.Client.UserInterface;

namespace Content.Client._DeepLagoon.Economy;

public sealed partial class MarketplaceUi : UIFragment
{
    private Control? _root;
    private EntityUid _owner;
    public override Control GetUIFragmentRoot() => _root!;
    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        if (_root is { Disposed: false } && _owner == userInterface.Owner) return;
        _owner = userInterface.Owner;
        _root = IoCManager.Resolve<IEntityManager>().System<PlayerTradeClientSystem>().OpenEmbedded(_owner);
    }
    public override void UpdateState(BoundUserInterfaceState state) { }
}

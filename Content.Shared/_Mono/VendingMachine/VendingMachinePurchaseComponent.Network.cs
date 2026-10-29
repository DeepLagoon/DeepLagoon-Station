using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._Mono.VendingMachine;

public sealed partial class VendingMachinePurchaseComponent
{
    // Retain the generated state's name and fields for serialization compatibility.
    [Serializable, NetSerializable]
    public sealed class VendingMachinePurchaseComponent_AutoState : IComponentState
    {
        public NetEntity PurchaseGrid;
        public double OriginalPurchasePrice;
    }
}

public sealed class VendingMachinePurchaseNetworkSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<VendingMachinePurchaseComponent, ComponentGetState>(OnGetState);
        SubscribeLocalEvent<VendingMachinePurchaseComponent, ComponentHandleState>(OnHandleState);
    }

    private void OnGetState(EntityUid uid, VendingMachinePurchaseComponent component, ref ComponentGetState args)
    {
        args.State = new VendingMachinePurchaseComponent.VendingMachinePurchaseComponent_AutoState
        {
            PurchaseGrid = TryGetNetEntity(component.PurchaseGrid, out var grid) ? grid.Value : NetEntity.Invalid,
            OriginalPurchasePrice = component.OriginalPurchasePrice,
        };
    }

    private void OnHandleState(EntityUid uid, VendingMachinePurchaseComponent component, ref ComponentHandleState args)
    {
        if (args.Current is not VendingMachinePurchaseComponent.VendingMachinePurchaseComponent_AutoState state)
            return;

        component.PurchaseGrid = EnsureEntity<VendingMachinePurchaseComponent>(state.PurchaseGrid, uid);
        component.OriginalPurchasePrice = state.OriginalPurchasePrice;
    }
}

using Content.Server.Power.Components;
using Robust.Server.Physics;

namespace Content.Server._DeepLagoon.Power;

public sealed partial class PoiPowerRestrictionSystem : EntitySystem
{
    private EntityQuery<PoiPowerRestrictionComponent> _restrictionQuery;

    public override void Initialize()
    {
        _restrictionQuery = GetEntityQuery<PoiPowerRestrictionComponent>();
        SubscribeLocalEvent<PoiPowerRestrictionComponent, PostGridSplitEvent>(OnGridSplit);
    }

    public bool IsBlocked(EntityUid source, EntityUid? grid)
    {
        return _restrictionQuery.TryComp(grid, out var restriction) && !restriction.NativeSources.Contains(source);
    }

    internal void UpdateSuppliers()
    {
        var query = AllEntityQuery<PoiRestrictedPowerSourceComponent, PowerSupplierComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var supplier, out var transform))
        {
            supplier.SetLocationBlocked(IsBlocked(uid, transform.GridUid));
        }
    }

    private void OnGridSplit(Entity<PoiPowerRestrictionComponent> original, ref PostGridSplitEvent args)
    {
        var grid = args.Grid;
        var restriction = EnsureComp<PoiPowerRestrictionComponent>(grid);

        original.Comp.NativeSources.RemoveWhere(source =>
        {
            if (!TryComp<TransformComponent>(source, out var transform) || transform.GridUid != grid)
                return false;

            restriction.NativeSources.Add(source);
            return true;
        });
    }

    internal void TransferAfterMerge(EntityUid keeper, PoiPowerRestrictionComponent? pulled)
    {
        if (pulled != null)
            EnsureComp<PoiPowerRestrictionComponent>(keeper).NativeSources.UnionWith(pulled.NativeSources);
    }
}

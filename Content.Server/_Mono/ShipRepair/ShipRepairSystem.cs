using Content.Server._NF.Shipyard;
using Content.Server.Shuttles.Components;
using Content.Shared._Mono.ShipRepair;
using Content.Shared._Mono.ShipRepair.Components;
using Robust.Server.Physics;

namespace Content.Server._Mono.ShipRepair;

public sealed partial class ShipRepairSystem : SharedShipRepairSystem
{
    [Dependency] private SharedEyeSystem _eye = default!;
    private SharedMapSystem _map => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ShuttleComponent, ShipBoughtEvent>(OnShipBought);
        SubscribeLocalEvent<InitRepairSnapshotComponent, MapInitEvent>(OnInitSnapshot);
        SubscribeLocalEvent<PostGridSplitEvent>(OnGridSplit);

        InitCommands();
        InitGhosts();
    }

    private void OnShipBought(Entity<ShuttleComponent> ent, ref ShipBoughtEvent ev)
    {
        GenerateRepairData(ent);
    }

    private void OnInitSnapshot(Entity<InitRepairSnapshotComponent> ent, ref MapInitEvent ev)
    {
        GenerateRepairData(ent);
    }

    private void OnGridSplit(ref PostGridSplitEvent ev)
    {
        var hasSnapshot = HasComp<ShipRepairDataComponent>(ev.OldGrid);
        var hasSources = TryComp<ShipRepairFragmentComponent>(ev.OldGrid, out var parentFragment);
        if (!hasSnapshot && !hasSources)
            return;

        var fragment = EnsureComp<ShipRepairFragmentComponent>(ev.Grid);
        if (hasSnapshot)
            fragment.SourceGrids.Add(GetNetEntity(ev.OldGrid));
        if (parentFragment != null)
            fragment.SourceGrids.UnionWith(parentFragment.SourceGrids);
        Dirty(ev.Grid, fragment);
    }
}

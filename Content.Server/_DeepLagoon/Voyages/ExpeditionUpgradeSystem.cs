using Content.Server.Salvage;
using Content.Server.Shuttles.Components;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Shared.Access.Systems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Robust.Shared.Map.Components;

namespace Content.Server._DeepLagoon.Voyages;

/// <summary>One-use authorization media; its sprite is shared with coordinate disks, not their FTL destination behavior.</summary>
[RegisterComponent]
public sealed partial class ExpeditionUpgradeDiskComponent : Component;

public sealed class ExpeditionUpgradeSystem : EntitySystem
{
    [Dependency] private SalvageSystem _salvage = default!;
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<ExpeditionUpgradeDiskComponent, AfterInteractEvent>(OnInstall);
    }

    private void OnInstall(Entity<ExpeditionUpgradeDiskComponent> disk, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target is not { } console ||
            !HasComp<ShuttleConsoleComponent>(console) || !_hands.IsHolding(args.User, disk.Owner, out _) ||
            TerminatingOrDeleted(disk) || EntityManager.IsQueuedForDeletion(disk)) return;

        args.Handled = true;
        if (!_access.IsAllowed(args.User, console))
        {
            _popup.PopupEntity(Loc.GetString("expedition-upgrade-denied"), console, args.User);
            return;
        }
        if (Transform(console).GridUid is not { } grid || !HasComp<MapGridComponent>(grid) || !HasComp<ShuttleComponent>(grid))
        {
            _popup.PopupEntity(Loc.GetString("expedition-upgrade-no-shuttle"), console, args.User);
            return;
        }
        if (_salvage.GridExpeditionsEnabled(grid))
        {
            _popup.PopupEntity(Loc.GetString("expedition-upgrade-already"), console, args.User);
            return;
        }
        if (!_salvage.SetGridExpeditions(grid, true))
        {
            _popup.PopupEntity(Loc.GetString("expedition-upgrade-busy"), console, args.User);
            return;
        }
        if (TryComp<ParkingShuttleComponent>(grid, out var parking)) parking.ExpeditionsEnabled = true;
        _popup.PopupEntity(Loc.GetString("expedition-upgrade-installed"), console, args.User);
        QueueDel(disk);
    }
}

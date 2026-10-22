using Content.Shared._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Administration;
using Content.Shared.Database;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{
    private void Environment(GridEditorEnvironmentRequest args, EntitySessionEventArgs sessionArgs)
    {
        var player = sessionArgs.SenderSession;
        if (!_admins.HasAdminFlag(player, AdminFlags.Mapping) || !_drafts.TryGetValue(player, out var draft) ||
            draft.Token != args.Token || !HasComp<MapGridComponent>(draft.Grid) || args.Setting is not ("power" or "gravity")) return;
        var environment = EnsureComp<GridEnvironmentComponent>(draft.Grid);
        if (args.Setting == "power") environment.AlwaysPowered = args.Enabled;
        else environment.ForceGravity = args.Enabled;
        EntityManager.System<GridEnvironmentSystem>().RefreshGrid(draft.Grid, environment);
        _logs.Add(LogType.AdminMessage, LogImpact.High,
            $"{player.Name} set grid environment {args.Setting}={args.Enabled} on {ToPrettyString(draft.Grid)} using grid editor");
        foreach (var (viewer, other) in _drafts)
            if (other.Grid == draft.Grid) PublishEnvironment(viewer, other);
    }

    private void PublishEnvironment(ICommonSession player, Draft draft)
    {
        TryComp<GridEnvironmentComponent>(draft.Grid, out var environment);
        RaiseNetworkEvent(new GridEditorEnvironmentState(draft.Token, environment?.AlwaysPowered ?? false,
            environment?.ForceGravity ?? false, environment?.SafeAtmosphere ?? false, HasComp<ApartmentProtectedGridComponent>(draft.Grid)), player);
    }
}

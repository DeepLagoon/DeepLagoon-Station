using System.Linq;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Administration;
using Content.Shared.Database;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{
    private void RepairConnections(GridEditorRepairConnectionsRequest args, EntitySessionEventArgs sessionArgs)
    {
        var player = sessionArgs.SenderSession;
        if (!_admins.HasAdminFlag(player, AdminFlags.Mapping) || !_drafts.TryGetValue((player, args.Token), out var draft) ||
            draft.Token != args.Token || !HasComp<MapGridComponent>(draft.Grid)) return;
        RaiseNetworkEvent(new GridEditorRepairConnectionsEvent(GetNetEntity(draft.Grid)));
        RaiseNetworkEvent(new GridEditorResultEvent("Пересчёт визуальных соединений grid запрошен у всех клиентов.", true, args.Token), player);
        _logs.Add(LogType.AdminMessage, LogImpact.Low,
            $"{player.Name} requested visual connection repair on {ToPrettyString(draft.Grid)} using grid editor");
    }

    private void RenameGrid(GridEditorNameRequest args, EntitySessionEventArgs sessionArgs)
    {
        var player = sessionArgs.SenderSession;
        if (!_admins.HasAdminFlag(player, AdminFlags.Mapping) || !_drafts.TryGetValue((player, args.Token), out var draft) ||
            draft.Token != args.Token || !HasComp<MapGridComponent>(draft.Grid)) return;
        var name = args.Name?.Trim() ?? "";
        if (name.Length is < 1 or > GridEditorNameRequest.MaxLength || name.Any(char.IsControl))
        {
            RaiseNetworkEvent(new GridEditorNameResult(draft.Token,
                $"Название должно содержать от 1 до {GridEditorNameRequest.MaxLength} символов без переносов строк и управляющих символов."), player);
            return;
        }
        var previous = MetaData(draft.Grid).EntityName;
        if (name != previous)
        {
            EntityManager.System<Robust.Shared.GameObjects.MetaDataSystem>().SetEntityName(draft.Grid, name);
            _logs.Add(LogType.AdminMessage, LogImpact.Low,
                $"{player.Name} renamed grid {ToPrettyString(draft.Grid)} from '{previous}' to '{name}' using grid editor");
        }
        foreach (var ((viewer, _), other) in _drafts)
            if (other.Grid == draft.Grid) PublishEnvironment(viewer, other);
        RaiseNetworkEvent(new GridEditorNameResult(draft.Token), player);
    }

    private void Environment(GridEditorEnvironmentRequest args, EntitySessionEventArgs sessionArgs)
    {
        var player = sessionArgs.SenderSession;
        if (!_admins.HasAdminFlag(player, AdminFlags.Mapping) || !_drafts.TryGetValue((player, args.Token), out var draft) ||
            draft.Token != args.Token || !HasComp<MapGridComponent>(draft.Grid) || args.Setting is not ("power" or "gravity")) return;
        var environment = EnsureComp<GridEnvironmentComponent>(draft.Grid);
        if (args.Setting == "power") environment.AlwaysPowered = args.Enabled;
        else environment.ForceGravity = args.Enabled;
        EntityManager.System<GridEnvironmentSystem>().RefreshGrid(draft.Grid, environment);
        _logs.Add(LogType.AdminMessage, LogImpact.High,
            $"{player.Name} set grid environment {args.Setting}={args.Enabled} on {ToPrettyString(draft.Grid)} using grid editor");
        foreach (var ((viewer, _), other) in _drafts)
            if (other.Grid == draft.Grid) PublishEnvironment(viewer, other);
    }

    private void PublishEnvironment(ICommonSession player, Draft draft)
    {
        TryComp<GridEnvironmentComponent>(draft.Grid, out var environment);
        RaiseNetworkEvent(new GridEditorEnvironmentState(draft.Token, environment?.AlwaysPowered ?? false,
            environment?.ForceGravity ?? false, environment?.SafeAtmosphere ?? false, HasComp<ApartmentProtectedGridComponent>(draft.Grid),
            MetaData(draft.Grid).EntityName), player);
    }
}

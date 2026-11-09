using System.Linq;
using Content.Server._Mono.NPC.HTN;
using Content.Server._NF.Shuttles.Components;
using Content.Server._NF.Shuttles.Systems;
using Content.Server.Salvage;
using Content.Server.Shuttles.Systems;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Administration;
using Content.Shared.Database;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{
    [Dependency] private SalvageSystem _salvage = default!;
    [Dependency] private ShuttleSystem _shuttles = default!;
    [Dependency] private ForceAnchorSystem _forceAnchor = default!;

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
        PublishEnvironment(draft.Grid);
        RaiseNetworkEvent(new GridEditorNameResult(draft.Token), player);
    }

    private void Environment(GridEditorEnvironmentRequest args, EntitySessionEventArgs sessionArgs)
    {
        var player = sessionArgs.SenderSession;
        if (!_admins.HasAdminFlag(player, AdminFlags.Mapping) || !_drafts.TryGetValue((player, args.Token), out var draft) ||
            draft.Token != args.Token || !HasComp<MapGridComponent>(draft.Grid)) return;

        var grid = draft.Grid;
        switch (args.Setting)
        {
            case "power":
            case "gravity":
                var environment = EnsureComp<GridEnvironmentComponent>(grid);
                if (args.Setting == "power")
                    environment.AlwaysPowered = args.Enabled;
                else
                    environment.ForceGravity = args.Enabled;
                EntityManager.System<GridEnvironmentSystem>().RefreshGrid(grid, environment);
                break;
            case "expeditions":
                if (!_salvage.SetGridExpeditions(grid, args.Enabled))
                {
                    RaiseNetworkEvent(new GridEditorResultEvent("Дождитесь завершения текущей экспедиции.", false, args.Token), player);
                    return;
                }
                break;
            case "ignoreAi":
                if (args.Enabled)
                    _tags.AddTag(grid, ShipTargetingSystem.IgnoreGridTag);
                else
                    _tags.RemoveTag(grid, ShipTargetingSystem.IgnoreGridTag);
                break;
            case "anchor":
                if (!_forceAnchor.SetForceAnchored(grid, args.Enabled))
                {
                    RaiseNetworkEvent(new GridEditorResultEvent("Закрепление недоступно во время FTL или у грида без физического тела.", false, args.Token), player);
                    return;
                }
                break;
            case "hideIff":
                if (args.Enabled)
                    _shuttles.AddIFFFlag(grid, IFFFlags.Hide, force: true);
                else
                    _shuttles.RemoveIFFFlag(grid, IFFFlags.Hide, force: true);
                break;
            case "radarColor":
                if (args.Value is not { Length: 7 } || args.Value[0] != '#' || !Color.TryFromHex(args.Value, out var color))
                    return;
                _shuttles.SetIFFColor(grid, color, force: true);
                _shuttles.AddIFFFlag(grid, IFFFlags.AlwaysShowColor, force: true);
                break;
            default:
                return;
        }

        var value = args.Setting == "radarColor" ? args.Value : args.Enabled.ToString();
        _logs.Add(LogType.AdminMessage, LogImpact.High,
            $"{player.Name} set grid environment {args.Setting}={value} on {ToPrettyString(grid)} using grid editor");
        PublishEnvironment(grid);
    }

    private void PublishEnvironment(EntityUid grid)
    {
        foreach (var ((viewer, _), other) in _drafts)
            if (other.Grid == grid) PublishEnvironment(viewer, other);
    }

    private void PublishEnvironment(ICommonSession player, Draft draft)
    {
        TryComp<GridEnvironmentComponent>(draft.Grid, out var environment);
        TryComp<IFFComponent>(draft.Grid, out var iff);
        RaiseNetworkEvent(new GridEditorEnvironmentState(draft.Token, environment?.AlwaysPowered ?? false,
            environment?.ForceGravity ?? false, environment?.SafeAtmosphere ?? false, HasComp<ApartmentProtectedGridComponent>(draft.Grid),
            MetaData(draft.Grid).EntityName, _salvage.GridExpeditionsEnabled(draft.Grid),
            _tags.HasTag(draft.Grid, ShipTargetingSystem.IgnoreGridTag), HasComp<ForceAnchorComponent>(draft.Grid),
            _shuttles.GetIFFColor(draft.Grid, component: iff).ToHexNoAlpha(),
            iff != null && (iff.Flags & IFFFlags.Hide) != 0), player);
    }
}

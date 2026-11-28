using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server._Mono.ShipRepair;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.Power;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Server.Power.EntitySystems;
using Robust.Shared.Player;

namespace Content.Server.Shuttles.Systems;

public sealed partial class ShuttleConsoleSystem
{
    private void OnRepairSnapshot(Entity<ShuttleConsoleComponent> ent, ref ShuttleRepairSnapshotMessage args)
    {
        if (!Transform(ent).Anchored || !this.IsPowered(ent.Owner, EntityManager) ||
            !_blocker.CanInteract(args.Actor, ent.Owner) || !_access.IsAllowed(args.Actor, ent.Owner) || !_tags.HasTag(args.Actor, CanPilotTag)) return;
        if (TryComp<ShuttleConsoleLockComponent>(ent, out var locked) &&
            EntityManager.System<Content.Shared.Shuttles.Systems.SharedShuttleConsoleLockSystem>().GetEffectiveLockState(ent.Owner, locked)) return;
        var ev = new ConsoleShuttleEvent { Console = ent.Owner };
        RaiseLocalEvent(ent.Owner, ref ev);
        if (ev.Console is not { } console || Transform(console).GridUid is not { } grid || !HasComp<ShuttleComponent>(grid)) return;
        if (!TryComp<ActorComponent>(args.Actor, out var actor) ||
            TryComp<ParkingShuttleComponent>(grid, out var parked) && parked.OwnerAccount != actor.PlayerSession.UserId.UserId ||
            TryComp<ShipOwnershipComponent>(grid, out var owned) && owned.OwnerUserId != actor.PlayerSession.UserId) return;
        var message = "";
        var repairs = EntityManager.System<ShipRepairSystem>();
        var data = EnsureComp<ShipRepairDataComponent>(grid);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (data.ShipyardLocked || EntityManager.System<ParkingSystem>().IsShipyardRepairing(grid) || repairs.HasActiveToolRepairs(grid))
            message = "Дождитесь завершения ремонта шаттла.";
        else if (data.SnapshotReadyAt > now) message = "Сохранение снимка доступно раз в 20 минут.";
        else
        {
            var oldChunks = data.Chunks;
            var oldPalette = data.EntityPalette;
            var oldVersion = data.SnapshotVersion;
            try
            {
                repairs.GenerateRepairData(grid);
                EntityManager.System<ParkingSystem>().CaptureRepairProvenance(grid);
                data.SnapshotReadyAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 20 * 60 * 1000;
                Dirty(grid, data);
                message = "Текущее состояние шаттла сохранено для Ship Repair Device. Котировки ремонта по старому снимку недействительны.";
            }
            catch (Exception error)
            {
                data.Chunks = oldChunks; data.EntityPalette = oldPalette; data.SnapshotVersion = oldVersion;
                Dirty(grid, data);
                message = "Не удалось сохранить снимок. Предыдущий снимок сохранён.";
                _sawmill.Error($"Repair snapshot failed: {error}");
            }
        }
        _ui.ServerSendUiMessage(ent.Owner, ShuttleConsoleUiKey.Key, new ShuttleRepairSnapshotState(data.SnapshotReadyAt, data.ShipyardLocked, message), args.Actor);
        foreach (var (viewerConsole, viewers) in _hullViewers)
            if (GetHullGrid(viewerConsole) == grid)
                foreach (var viewer in viewers)
                    if (viewerConsole != ent.Owner || viewer != args.Actor)
                        SendRepairSnapshotState(viewerConsole, viewer, grid);
    }

    private void SendRepairSnapshotState(EntityUid console, EntityUid actor, EntityUid? grid)
    {
        var data = grid is { } uid ? CompOrNull<ShipRepairDataComponent>(uid) : null;
        _ui.ServerSendUiMessage(console, ShuttleConsoleUiKey.Key, new ShuttleRepairSnapshotState(data?.SnapshotReadyAt ?? 0, data?.ShipyardLocked ?? false), actor);
    }
}

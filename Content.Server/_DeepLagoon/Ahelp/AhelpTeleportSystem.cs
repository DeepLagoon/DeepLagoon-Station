using System.Linq;
using System.Numerics;
using Content.Server.Administration.Managers;
using Content.Server.GameTicking;
using Content.Server.Players.RateLimiting;
using Content.Server.Shuttles.Components;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Content.Shared.Follower;
using Content.Shared.Follower.Components;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared.Players.RateLimiting;
using Content.Shared.Shuttles.Components;
using Content.Shared._DeepLagoon.Ahelp;
using Content.Shared._NF.Shipyard.Components;
using Robust.Server.Player;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Server._DeepLagoon.Ahelp;

public sealed class AhelpTeleportSystem : EntitySystem
{
    private const string Rate = "AhelpNavigation";
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private PlayerRateLimitManager _rate = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedMapSystem _maps = default!;

    public override void Initialize()
    {
        _rate.Register(Rate, new RateLimitRegistration(CCVars.AhelpRateLimitPeriod, CCVars.AhelpRateLimitCount, null));
        SubscribeNetworkEvent<AhelpTeleportRequest>(OnRequest);
    }

    private void OnRequest(AhelpTeleportRequest request, EntitySessionEventArgs args)
    {
        var admin = args.SenderSession;
        if (request.Target == null || request.Target.Length > 8)
            return;
        if (_admins.GetAdminData(admin)?.HasFlag(AdminFlags.Admin) != true)
        {
            RaiseNetworkEvent(new AhelpTeleportState(request.Player, "Недостаточно прав для телепортации.",
                "Недостаточно прав для телепортации.", "Недостаточно прав для телепортации."), admin.Channel);
            return;
        }
        if (_rate.CountAction(admin, Rate) != RateLimitStatus.Allowed)
            return;
        var state = Inspect(request.Player);
        var result = "";
        if (request.Target.Length > 0)
        {
            var target = Resolve(new NetUserId(request.Player), request.Target);
            if (target.Error.Length > 0)
                result = target.Error;
            else if (admin.AttachedEntity is not { } entity || Deleted(entity))
                result = "У вас нет тела или призрака для телепортации.";
            else if (target.Entity is not { } destination || !TryCoordinates(destination, request.Target == "ship", out var coordinates))
                result = "Цель телепортации больше недоступна.";
            else
            {
                EntityManager.System<AhelpFollowSystem>().Cancel(admin.UserId);
                if (TryComp<FollowerComponent>(entity, out var follower))
                    EntityManager.System<FollowerSystem>().StopFollowingEntity(entity, follower.Following);
                _transform.SetCoordinates(entity, coordinates);
                _transform.AttachToGridOrMap(entity);
                if (TryComp<PhysicsComponent>(entity, out var physics))
                    EntityManager.System<SharedPhysicsSystem>().SetLinearVelocity(entity, Vector2.Zero, body: physics);
                result = request.Target switch
                {
                    "ship" => "Вы телепортированы на корабль игрока.",
                    "body" => "Вы телепортированы к телу игрока.",
                    _ => "Вы телепортированы к призраку игрока.",
                };
            }
        }
        RaiseNetworkEvent(new AhelpTeleportState(request.Player, state.ShipError, state.BodyError, state.GhostError, result), admin.Channel);
    }

    internal AhelpTeleportState Inspect(Guid player)
    {
        var target = new NetUserId(player);
        return new AhelpTeleportState(player, Resolve(target, "ship").Error, Resolve(target, "body").Error, Resolve(target, "ghost").Error);
    }

    internal (EntityUid? Entity, string Error) Resolve(NetUserId player, string kind)
    {
        if (player.UserId == Guid.Empty)
            return (null, "Выберите игрока.");
        if (kind is not ("ship" or "body" or "ghost"))
            return (null, "Неизвестная цель телепортации.");
        var mindSystem = EntityManager.System<SharedMindSystem>();
        mindSystem.TryGetMind(player, out _, out var mind);
        var attached = _players.TryGetSessionById(player, out var session) ? session.AttachedEntity : null;
        var body = mind?.OwnedEntity;
        if (!Valid(body) || HasComp<GhostComponent>(body))
            body = Valid(attached) && !HasComp<GhostComponent>(attached) ? attached : null;
        var ghost = new[] { attached, mind?.VisitingEntity, mind?.OwnedEntity }
            .FirstOrDefault(entity => Valid(entity) && HasComp<GhostComponent>(entity));
        EntityUid? target = kind switch { "body" => body, "ghost" => ghost, _ => FindShip(player, body, attached) };
        if (Valid(target))
            return TryCoordinates(target!.Value, kind == "ship", out _)
                ? (target, "") : (null, "Для цели нет доступной точки телепортации.");
        if (kind == "ship")
        {
            var parked = EntityQueryEnumerator<ParkingShuttleComponent>();
            while (parked.MoveNext(out _, out var ship))
            {
                if (ship.OwnerAccount == player.UserId && ship.Parked)
                    return (null, "Корабль игрока находится на парковке.");
            }
        }
        if (session != null && EntityManager.System<GameTicker>().PlayerGameStatuses.TryGetValue(player, out var status) && status != PlayerGameStatus.JoinedGame)
            return (null, "Игрок в лобби.");
        if (session == null && body == null && ghost == null)
            return (null, "Игрок отключён; цель телепортации отсутствует.");
        return (null, kind switch
        {
            "ship" => "Корабль игрока не найден.",
            "body" => "У игрока нет тела.",
            _ => "У игрока нет призрака.",
        });
    }

    private bool Valid(EntityUid? entity) => entity is { } uid && !TerminatingOrDeleted(uid);

    private EntityUid? FindShip(NetUserId player, EntityUid? body, EntityUid? attached)
    {
        // Prefer the player's current ship, then their registered active vessel.
        foreach (var entity in new[] { body, attached })
        {
            if (!Valid(entity)) continue;
            if (Transform(entity!.Value).GridUid is { } current && UsableShip(current))
                return current;
            if (TryComp<PilotComponent>(entity, out var pilot) && Valid(pilot.Console) &&
                Transform(pilot.Console!.Value).GridUid is { } remote && UsableShip(remote))
                return remote;
        }
        var ships = EntityQueryEnumerator<ShipOwnershipComponent>();
        while (ships.MoveNext(out var uid, out var ownership))
        {
            if (ownership.OwnerUserId == player && UsableShip(uid)) return uid;
        }
        var parked = EntityQueryEnumerator<ParkingShuttleComponent>();
        while (parked.MoveNext(out var uid, out var ownership))
        {
            if (ownership.OwnerAccount == player.UserId && UsableShip(uid)) return uid;
        }
        return null;
    }

    private bool UsableShip(EntityUid uid) => Valid(uid) && HasComp<ShuttleComponent>(uid) &&
        HasComp<MapGridComponent>(uid) && (!TryComp<ParkingShuttleComponent>(uid, out var parking) || !parking.Parked);

    private bool TryCoordinates(EntityUid destination, bool ship, out EntityCoordinates coordinates)
    {
        coordinates = default;
        if (!Valid(destination) || Transform(destination).MapUid == null)
            return false;
        if (!ship)
        {
            coordinates = Transform(destination).Coordinates;
            return true;
        }
        if (!TryComp<MapGridComponent>(destination, out var grid))
            return false;
        var tile = _maps.GetAllTiles(destination, grid).Where(tile => !tile.Tile.IsEmpty)
            .OrderBy(tile => tile.GridIndices.X * (long) tile.GridIndices.X + tile.GridIndices.Y * (long) tile.GridIndices.Y).FirstOrDefault();
        if (tile.Tile.IsEmpty)
            return false;
        coordinates = new EntityCoordinates(destination, (tile.GridIndices + new Vector2(0.5f)) * grid.TileSize);
        return true;
    }
}

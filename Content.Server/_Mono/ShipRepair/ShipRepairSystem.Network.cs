using System.Numerics;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.Hands.EntitySystems;
using Robust.Shared.GameStates;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Mono.ShipRepair;

public sealed partial class ShipRepairSystem
{
    [Dependency] private SharedHandsSystem _repairHands = default!;
    [Dependency] private IGameTiming _repairTiming = default!;
    private static readonly TimeSpan RepairInterestInterval = TimeSpan.FromSeconds(0.25);
    private TimeSpan _nextRepairInterestUpdate;
    private readonly Dictionary<ICommonSession, HashSet<EntityUid>> _repairInterests = new();
    private readonly HashSet<ICommonSession> _repairViewers = new();
    private readonly HashSet<EntityUid> _nearbyRepairGrids = new();
    private List<Entity<MapGridComponent>> _repairGrids = new();
    private readonly List<ICommonSession> _expiredRepairViewers = new();

    private void InitNetworkInterest()
    {
        SubscribeLocalEvent<ShipRepairDataComponent, ComponentGetStateAttemptEvent>(OnRepairDataStateAttempt);
    }

    private void OnRepairDataStateAttempt(Entity<ShipRepairDataComponent> ent,
        ref ComponentGetStateAttemptEvent args)
    {
        if (args.Player == null || !_repairInterests.TryGetValue(args.Player, out var grids)
                                || !grids.Contains(ent.Owner))
            args.Cancelled = true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_repairTiming.CurTime < _nextRepairInterestUpdate)
            return;
        _nextRepairInterestUpdate = _repairTiming.CurTime + RepairInterestInterval;
        _repairViewers.Clear();

        var viewers = EntityQueryEnumerator<ActorComponent, RepairDataEyeComponent, TransformComponent>();
        while (viewers.MoveNext(out var uid, out var actor, out _, out var xform))
        {
            var range = 0f;
            foreach (var hand in _repairHands.EnumerateHands(uid))
            {
                if (TryComp<ShipRepairToolComponent>(hand.HeldEntity, out var tool))
                    range = MathF.Max(range, MathF.Max(4f, tool.GhostRenderRadius));
            }
            if (range <= 0f || xform.MapUid == null)
                continue;

            var session = actor.PlayerSession;
            _repairViewers.Add(session);
            if (!_repairInterests.TryGetValue(session, out var interested))
                _repairInterests.Add(session, interested = new HashSet<EntityUid>());

            _nearbyRepairGrids.Clear();
            _repairGrids.Clear();
            var position = _transform.GetMapCoordinates(uid, xform);
            _map.FindGridsIntersecting(position.MapId,
                Box2.CenteredAround(position.Position, new Vector2(range * 2f)), ref _repairGrids,
                approx: true, includeMap: false);
            foreach (var grid in _repairGrids)
            {
                if (HasComp<ShipRepairDataComponent>(grid))
                    _nearbyRepairGrids.Add(grid);
            }
            // The shared repair tool also targets its own grid, including a missing edge tile.
            if (xform.GridUid is { } ownGrid && HasComp<ShipRepairDataComponent>(ownGrid))
                _nearbyRepairGrids.Add(ownGrid);

            foreach (var grid in _nearbyRepairGrids)
            {
                if (interested.Add(grid))
                {
                    // A newly interested receiver may have ACKed far beyond this static
                    // snapshot's last modification. Make the first delivery eligible again.
                    Dirty(grid, Comp<ShipRepairDataComponent>(grid));
                }
            }
            interested.IntersectWith(_nearbyRepairGrids);
        }

        _expiredRepairViewers.Clear();
        foreach (var session in _repairInterests.Keys)
        {
            if (!_repairViewers.Contains(session))
                _expiredRepairViewers.Add(session);
        }
        foreach (var session in _expiredRepairViewers)
            _repairInterests.Remove(session);
    }
}

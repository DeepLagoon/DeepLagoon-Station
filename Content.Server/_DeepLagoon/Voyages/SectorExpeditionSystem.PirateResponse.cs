using Content.Shared._DeepLagoon.Voyages;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;

namespace Content.Server._DeepLagoon.Voyages;

public sealed partial class SectorExpeditionSystem
{
    private static readonly TimeSpan PirateReactionDelay = TimeSpan.FromSeconds(10);

    private void RegisterPirateArrival(Trip trip, EntityUid map)
    {
        // Returning from a planet in the same sector must not repeatedly pacify its pirates.
        if (trip.PirateAlertMap == map) return;
        trip.PirateAlertMap = map;
        trip.PirateAttackAt = _timing.CurTime + PirateReactionDelay;
    }

    private bool PirateAttackAllowed(Trip trip, EntityUid map)
    {
        RegisterPirateArrival(trip, map);
        return _timing.CurTime >= trip.PirateAttackAt;
    }

    private bool InPirateTransit(EntityUid ship) =>
        TryComp<FTLComponent>(ship, out var ftl) && ftl.State != FTLState.Cooldown;

    private void OnSectorGunShot(Entity<GunComponent> weapon, ref GunShotEvent args)
    {
        if (!Enabled || _trips.Count == 0 || args.Ammo.Count == 0 ||
            Transform(weapon).MapUid is not { } map || !HasComp<SectorExpeditionMapComponent>(map)) return;
        var sourceGrid = Transform(weapon).GridUid;
        foreach (var trip in _trips.Values)
        {
            if (trip.Wiped || !Exists(trip.Ship) || Transform(trip.Ship).MapUid != map || InPirateTransit(trip.Ship) ||
                sourceGrid != trip.Ship && !trip.Bodies.Contains(args.User)) continue;
            trip.PirateAlertMap = map;
            trip.PirateAttackAt = TimeSpan.Zero;
            // Let the regular AI update react on the next tick instead of waiting for its polling interval.
            _next = TimeSpan.Zero;
        }
    }
}

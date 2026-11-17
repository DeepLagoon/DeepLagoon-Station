using System.Numerics;
using Content.Server.Pinpointer;
using Content.Shared.IdentityManagement;
using Content.Shared.Materials.OreSilo;
using Robust.Server.GameStates;
using Robust.Shared.Player;

namespace Content.Server.Materials;

/// <inheritdoc/>
public sealed partial class OreSiloSystem : SharedOreSiloSystem
{
    [Dependency] private EntityLookupSystem _entityLookup = default!;
    [Dependency] private NavMapSystem _navMap = default!;
    [Dependency] private PvsOverrideSystem _pvsOverride = default!;
    [Dependency] private SharedUserInterfaceSystem _userInterface = default!;

    private const float OreSiloPreloadRangeSquared = 225f; // ~1 screen

    private readonly HashSet<Entity<OreSiloClientComponent>> _clientLookup = new();
    private readonly HashSet<(NetEntity, string, string)> _clientInformation = new();
    private readonly HashSet<EntityUid> _silosToAdd = new();
    private readonly HashSet<EntityUid> _silosToRemove = new();

    // Mono
    private readonly List<(EntityUid? Grid, Vector2 Position, EntityUid Silo)> _silos = new();

    // Mono
    private float _updateAccumulator = 0f;
    private float _updateInterval = 1f;
    private bool _reportedInvalidSilo;

    protected override void UpdateOreSiloUi(Entity<OreSiloComponent> ent)
    {
        if (!_userInterface.IsUiOpen(ent.Owner, OreSiloUiKey.Key))
            return;
        _clientLookup.Clear();
        _clientInformation.Clear();

        var xform = Transform(ent);

        // Sneakily uses override with TComponent parameter
        _entityLookup.GetEntitiesInRange(xform.Coordinates, ent.Comp.Range, _clientLookup);

        foreach (var client in _clientLookup)
        {
            // don't show already-linked clients.
            if (client.Comp.Silo is not null)
                continue;

            // Don't show clients on the screen if we can't link them.
            if (!CanTransmitMaterials((ent, ent, xform), client))
                continue;

            var netEnt = GetNetEntity(client);
            var name = Identity.Name(client, EntityManager);
            var beacon = _navMap.GetNearestBeaconString(client.Owner, onlyName: true);
            var inRange = CanTransmitMaterials((ent, ent, xform), client);

            var txt = Loc.GetString("ore-silo-ui-itemlist-entry",
                ("name", name),
                ("beacon", beacon),
                ("linked", ent.Comp.Clients.Contains(client)),
                ("inRange", true));

            _clientInformation.Add((netEnt, txt, beacon));
        }

        // Get all clients of this silo, including those out of range.
        foreach (var client in ent.Comp.Clients)
        {
            var netEnt = GetNetEntity(client);
            var name = Identity.Name(client, EntityManager);
            var beacon = _navMap.GetNearestBeaconString(client, onlyName: true);
            var inRange = CanTransmitMaterials((ent, ent), client);

            var txt = Loc.GetString("ore-silo-ui-itemlist-entry",
                ("name", name),
                ("beacon", beacon),
                ("linked", ent.Comp.Clients.Contains(client)),
                ("inRange", inRange));

            _clientInformation.Add((netEnt, txt, beacon));
        }

        _userInterface.SetUiState(ent.Owner, OreSiloUiKey.Key, new OreSiloBuiState(_clientInformation));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _updateAccumulator += frameTime;
        if (_updateAccumulator < _updateInterval)
            return;
        _updateAccumulator -= _updateInterval;

        // Solving an annoying problem: we need to send the silo to people who are near the silo so that
        // Things don't start wildly mispredicting. We do this as cheaply as possible via grid-based local-pos checks.
        // Sloth okay-ed this in the interim until a better solution comes around.

        _silos.Clear();
        var clientQuery = EntityQueryEnumerator<OreSiloClientComponent>();
        while (clientQuery.MoveNext(out var uid, out var siloComp))
        {
            if (siloComp.Silo is not { } silo)
                continue;

            if (!silo.IsValid())
            {
                if (!_reportedInvalidSilo)
                {
                    _reportedInvalidSilo = true;
                    Log.Warning($"Ignoring invalid silo link {silo} on {ToPrettyString(uid)} while preloading PVS.");
                }
                continue;
            }

            var transform = Transform(uid);
            _silos.Add((transform.GridUid, transform.LocalPosition, silo));
        }

        if (_silos.Count == 0)
            return;

        var actorQuery = EntityQueryEnumerator<ActorComponent>();
        while (actorQuery.MoveNext(out var actorUid, out var actorComp))
        {
            _silosToAdd.Clear();
            _silosToRemove.Clear();

            var actorXform = Transform(actorUid);

            foreach (var (grid, position, silo) in _silos)
            {
                // We limit it to same-grid checks only for peak perf
                if (actorXform.GridUid != grid)
                    continue;

                if (Vector2.DistanceSquared(actorXform.LocalPosition, position) <= OreSiloPreloadRangeSquared)
                {
                    _silosToAdd.Add(silo);
                }
                else
                {
                    _silosToRemove.Add(silo);
                }
            }

            foreach (var toRemove in _silosToRemove)
            {
                _pvsOverride.RemoveSessionOverride(toRemove, actorComp.PlayerSession);
            }
            foreach (var toAdd in _silosToAdd)
            {
                _pvsOverride.AddSessionOverride(toAdd, actorComp.PlayerSession);
            }
        }
    }
}

using Content.Server._DeepLagoon.Mapping;
using Content.Server.GameTicking;
using Content.Server.Shuttles.Systems;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.GameTicking;
using Content.Shared.Whitelist;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._DeepLagoon.Apartments;

/// <summary>One residential hub per round, on its own space map, independent of the station map pool.</summary>
public sealed partial class ApartmentStopLoaderSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private GameTicker _ticker = default!;
    private SharedMapSystem _maps => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private ShuttleSystem _shuttles = default!;
    [Dependency] private ApartmentTravelSystem _travel = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private GridEnvironmentSystem _environment = default!;

    private bool _attempted;
    private EntityUid? _hubMap;
    private TimeSpan _nextCheck;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => ResetRound());
        // Apply on the following update, after all round-start handlers have finished.
        SubscribeLocalEvent<RoundStartedEvent>(_ => ResetRound());
        Subs.CVar(_config, ApartmentCVars.Enabled, enabled =>
        {
            if (enabled) _attempted = false;
        });
    }

    private void EnsureSafeAtmosphere(EntityUid grid)
    {
        var environment = EnsureComp<GridEnvironmentComponent>(grid);
        environment.SafeAtmosphere = true;
        _environment.RefreshGrid(grid, environment);
    }

    private void ResetRound()
    {
        _attempted = false;
        _hubMap = null;
        _nextCheck = TimeSpan.Zero;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_attempted || !_config.GetCVar(ApartmentCVars.Enabled) ||
            _ticker.RunLevel != GameRunLevel.InRound || _timing.CurTime < _nextCheck) return;
        _nextCheck = _timing.CurTime + TimeSpan.FromSeconds(1);
        _attempted = true;

        // Toggling the feature or removing its marker must not spawn another copy of our hub.
        if (_hubMap is { } existing && !Deleted(existing)) return;
        var stops = EntityQueryEnumerator<ApartmentTransitStopComponent>();
        while (stops.MoveNext(out var marker, out _))
        {
            if (!_travel.ValidStop(marker)) continue;
            if (Transform(marker).GridUid is { } existingGrid) EnsureSafeAtmosphere(existingGrid);
            Log.Info($"Using existing residential stop {ToPrettyString(marker)}.");
            return;
        }

        var path = new ResPath(_config.GetCVar(ApartmentCVars.StopMap));
        var map = _maps.CreateMap(out var mapId);
        var retained = false;
        try
        {
            if (!_loader.TryLoadGridTracked(mapId, path, out var grid))
            {
                Log.Error($"Could not load residential stop from {path}. Toggle apartments_enabled to retry after fixing the map.");
                return;
            }

            // Reject a wrong map instead of leaving an unreachable grid or repeatedly loading it.
            var markers = EntityQueryEnumerator<ApartmentTransitStopComponent, TransformComponent>();
            while (markers.MoveNext(out var marker, out _, out var transform))
            {
                if (transform.GridUid != grid.Value.Owner || !_travel.ValidStop(marker)) continue;
                EnsureSafeAtmosphere(grid.Value.Owner);
                // A standalone map does not receive StationPostInitEvent, which normally
                // registers station maps. FTLToDock silently rejects unregistered maps.
                if (!_shuttles.TryAddFTLDestination(mapId, true, false, false, out var destination))
                {
                    Log.Error($"Could not register residential stop map {mapId} as an FTL destination.");
                    return;
                }
                _shuttles.SetFTLWhitelist((map, destination), new EntityWhitelist
                {
                    Components = new[] { "TransitShuttle" },
                });
                _hubMap = map;
                retained = true;
                Log.Info($"Loaded residential stop {ToPrettyString(grid.Value.Owner)} on isolated space map {mapId} from {path}.");
                return;
            }
            Log.Error($"Residential stop map {path} has no valid anchored apartment stop marker.");
        }
        finally
        {
            // Deleting the failed map also deletes any partially loaded grid and contents.
            if (!retained) _maps.DeleteMap(mapId);
        }
        // ApartmentTravelSystem registers the marker with the existing cross-map bus route.
    }
}

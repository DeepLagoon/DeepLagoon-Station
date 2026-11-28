using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._DeepLagoon.Mapping;
using Content.Server._Mono.FireControl;
using Content.Server._Mono.NPC.HTN;
using Content.Server._Mono.Radar;
using Content.Server.Cargo.Components;
using Content.Server.Salvage;
using Content.Shared._DeepLagoon.Voyages;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC.Components;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using YamlDotNet.RepresentationModel;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared._FarHorizons.StarSystem;
using Robust.Shared.ContentPack;
namespace Content.Server._DeepLagoon.Voyages;
public sealed partial class SectorExpeditionSystem
{
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private ShipSteeringSystem _steering = default!;
    [Dependency] private ShipTargetingSystem _targeting = default!;
    [Dependency] private Content.Shared.Construction.EntitySystems.AnchorableSystem _anchor = default!;
    [Dependency] private IResourceManager _resources = default!;
    private sealed class PirateShip
    {
        public required EntityUid Grid, Core;
        public required int Index;
        public required Guid Encounter;
        public readonly List<EntityUid> Defenders = new();
        public bool Counted;
    }
    private readonly Dictionary<string, int> _combatCatalogue = new();
    private readonly HashSet<string> _encounterBusy = new();
    private Vector2 PlanetPosition(string sector, int index)
    {
        var seed = _voyages.WorldSnapshot.Seed; var hash = VoyageGalaxy.Hash(sector + "/position/" + index, seed);
        var angle = (hash % 10000) / 10000f * MathF.Tau;
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (400 + index * 230);
    }
    private Orbit GetOrbit(string sector)
    {
        if (_orbits.TryGetValue(sector, out var existing) && Exists(existing.Map)) return existing;
        if (_orbits.Count >= Math.Clamp(_config.GetCVar(SectorExpeditionCVars.MaxLoaded), 1, 64)) throw new InvalidOperationException("Все исследовательские сектора заняты. Дождитесь возвращения одного из экипажей");
        var map = _maps.CreateMap(out _); EnsureComp<SectorExpeditionMapComponent>(map).Sector = sector;
        Comp<SectorExpeditionMapComponent>(map).Cycle = _voyages.WorldSnapshot.Cycle;
        EntityManager.System<MetaDataSystem>().SetEntityName(map, _voyages.RouteGraph.System(sector).Name);
        var orbit = new Orbit { Map = map, Sector = sector }; _orbits[sector] = orbit;
        var count = SectorResearchDefinition.Settled(sector) ? 0 : SectorResearchDefinition.Count(sector, _voyages.WorldSnapshot.Seed);
        var definition = new SectorDefinition { Id = sector, Seed = (uint)_voyages.WorldSnapshot.Seed, NavigationOnly = true };
        for (var i = 0; i < count; i++)
        {
            var marker = Spawn(null, new EntityCoordinates(map, PlanetPosition(sector, i)));
            // A navigation coordinate must never become cargo when a shuttle flies over it.
            Transform(marker).GridTraversal = false;
            var name = PlanetName(sector, i); EntityManager.System<MetaDataSystem>().SetEntityName(marker, name);
            EnsureComp<SectorLandmarkComponent>(marker); orbit.Markers[i] = marker;
            var position = PlanetPosition(sector, i);
            definition.Planets.Add(new SectorPlanet { Id = "planet-" + i, Name = name, Template = PlanetType(sector, i), X = position.X, Y = position.Y,
                AppearanceSeed = (uint)SectorResearchDefinition.PlanetSeed(sector, i, _voyages.WorldSnapshot.Seed) });
        }
        // Publish the native planet manifest directly. SetSystem() would spawn extra landmarks/light and assign a generated environment.
        var navigation = EnsureComp<StarSystemMapComponent>(map);
        navigation.System = "SystemKyphrus"; navigation.Definition = definition;
        navigation.StarSystem = EntityManager.System<Content.Server._FarHorizons.StarSystem.StarSystemMapSystem>().BuildPlanetarySystem("SystemKyphrus", definition);
        Dirty(map, navigation);
        if (!SectorResearchDefinition.Settled(sector)) _ = PopulatePirates(orbit); return orbit;
    }
    private string PlanetName(string sector, int index) => _salvage.GetFTLName(_prototypes.Index<Content.Shared.Dataset.LocalizedDatasetPrototype>("NamesBorer"), SectorResearchDefinition.PlanetSeed(sector, index, _voyages.WorldSnapshot.Seed));
    private int CombatScore(VesselPrototype vessel)
    {
        if (_combatCatalogue.TryGetValue(vessel.ID, out var cached)) return cached;
        try
        {
            using var stream = _resources.ContentFileReadText(vessel.ShuttlePath);
            var yaml = new YamlStream(); yaml.Load(stream);
            var root = (YamlMappingNode)yaml.Documents[0].RootNode; var guns = 0;
            if (root.Children.TryGetValue(new YamlScalarNode("entities"), out var entries) && entries is YamlSequenceNode sequence)
                foreach (var entry in sequence.Children.OfType<YamlMappingNode>())
                    if (entry.Children.TryGetValue(new YamlScalarNode("proto"), out var value) && value is YamlScalarNode { Value: { } id } &&
                        _prototypes.TryIndex<EntityPrototype>(id, out var prototype) && prototype.TryGetComponent<FireControllableComponent>(out _))
                        guns += entry.Children.TryGetValue(new YamlScalarNode("entities"), out var instances) && instances is YamlSequenceNode group ? group.Children.Count : 1;
            var text = vessel.Description.ToLowerInvariant();
            var military = text.Contains("оруди") || text.Contains("вооруж") || text.Contains("боев") || text.Contains("gun") || text.Contains("combat");
            return _combatCatalogue[vessel.ID] = guns == 0 ? 0 : guns * 10 + (int)vessel.Category * 8 + (military ? 5 : 0);
        }
        catch (Exception e) { Log.Warning($"Pirate vessel classification skipped {vessel.ID}: {e.Message}"); return _combatCatalogue[vessel.ID] = 0; }
    }
    private async Task PopulatePirates(Orbit orbit)
    {
        if (!_encounterBusy.Add(orbit.Sector)) return;
        try
        {
            var graph = _voyages.RouteGraph; var path = graph.System(orbit.Sector).Known ? graph.Path(VoyageRouteModel.Home, orbit.Sector) : graph.SurveyPath(orbit.Sector);
            var world = _voyages.WorldSnapshot; var pressures = Enumerable.Range(1, Math.Max(0, path.Length - 1)).Select(i => world.Routes.GetValueOrDefault(VoyageGalaxy.LaneKey(path[i - 1], path[i]))?.Value(Now) ?? .5).ToArray();
            var heat = pressures.Length == 0 ? .35 : pressures.Max();
            SectorEncounterInfo encounter;
#if DEBUG
            if (_testEncounters.TryGetValue(orbit.Sector, out var testCount))
            {
                encounter = new(Guid.NewGuid(), Enumerable.Range(0, testCount).ToArray());
                heat = 0; orbit.TestEncounter = true;
            }
            else
#endif
                encounter = await _db.SectorEncounterAsync(world.Cycle, orbit.Sector, path, heat, Now, Guid.Empty, -1);
            if (!Exists(orbit.Map) || encounter.Remaining.Length == 0) return;
            var catalogue = _prototypes.EnumeratePrototypes<VesselPrototype>().Where(v => !v.Abstract && v.Group == ShipyardConsoleUiKey.Syndicate)
                .Select(v => (Vessel: v, Score: CombatScore(v))).Where(v => v.Score > 0).OrderBy(v => v.Score).ToArray();
            if (catalogue.Length == 0) { Log.Error("No armed syndicate catalogue vessels are available for sector encounters."); return; }
            foreach (var index in encounter.Remaining)
            {
                var tier = Math.Clamp((int)(heat * (catalogue.Length - 1)), 0, catalogue.Length - 1);
                var vessel = catalogue[Math.Clamp(tier + index % 2, 0, catalogue.Length - 1)].Vessel;
                var angle = index * MathF.Tau / 3; var offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (650 + index * 120);
                if (!_loader.TryLoadGridTracked(Comp<MapComponent>(orbit.Map).MapId, vessel.ShuttlePath, out var grid, offset: offset))
                { Log.Error($"Sector pirate hull could not load: sector={orbit.Sector}, vessel={vessel.ID}."); continue; }
                EnsureComp<CargoSellBlacklistComponent>(grid.Value.Owner);
                _shuttles.SetIFFColor(grid.Value.Owner, Color.Red, force: true);
                _shuttles.AddIFFFlag(grid.Value.Owner, Content.Shared.Shuttles.Components.IFFFlags.AlwaysShowColor, force: true);
                _shuttles.RemoveIFFFlag(grid.Value.Owner, Content.Shared.Shuttles.Components.IFFFlags.Hide |
                    Content.Shared.Shuttles.Components.IFFFlags.HideLabelAlways | Content.Shared.Shuttles.Components.IFFFlags.HideLabel, force: true);
                EntityManager.System<MetaDataSystem>().SetEntityName(grid.Value.Owner, "Пиратский " + vessel.LocalizedName);
                var tiles = _maps.GetAllTiles(grid.Value.Owner, grid.Value.Comp).Where(t => !t.Tile.IsEmpty && _anchor.TileFree(grid.Value.Comp, t.GridIndices, (int)Content.Shared.Physics.CollisionGroup.MobLayer, (int)Content.Shared.Physics.CollisionGroup.MobMask)).Select(t => _maps.GridTileToLocal(grid.Value.Owner, grid.Value.Comp, t.GridIndices)).ToArray();
                if (tiles.Length == 0) { Log.Error($"Sector pirate hull has no defender spawn tiles: sector={orbit.Sector}, vessel={vessel.ID}."); QueueDel(grid.Value.Owner); continue; }
                var core = Spawn("NpcStationAiAttackerSmart", tiles[tiles.Length / 2]);
                RemComp<Content.Server.NPC.HTN.HTNComponent>(core);
                _transform.AnchorEntity(core);
                var pirate = new PirateShip { Grid = grid.Value.Owner, Core = core, Index = index, Encounter = encounter.Id };
                var defenders = Math.Clamp(2 + (int)vessel.Category + (int)(heat * 3), 3, 9);
                for (var i = 0; i < defenders; i++)
                {
                    var prototype = i == 0 ? "MobSyndicateNavalCaptainA" : i == 1 ? "MobSyndicateNavalEngineerA" : "MobSyndicateNavalOperatorA";
                    if (!_prototypes.HasIndex<EntityPrototype>(prototype)) prototype = "MobSyndicateNavalCaptainA";
                    var guard = Spawn(prototype, tiles[(i * 17 + tiles.Length / 3) % tiles.Length]);
                    EntityManager.System<ExpeditionSalvageSystem>().ProtectDefender(guard); pirate.Defenders.Add(guard);
                }
                EntityManager.System<ExpeditionSalvageSystem>().ProtectDefender(core); orbit.Pirates.Add(pirate);
                Log.Info($"Sector pirate spawned: sector={orbit.Sector}, map={orbit.Map}, vessel={vessel.ID}, grid={grid.Value.Owner}, core={core}, defenders={pirate.Defenders.Count}, offset={offset}.");
            }
        }
        catch (Exception e) { Log.Error($"Sector pirate spawn {orbit.Sector} failed: {e.Message}"); }
        finally { _encounterBusy.Remove(orbit.Sector); }
    }
    private void UpdatePirates()
    {
        foreach (var orbit in _orbits.Values)
        {
            var targets = _trips.Values.Where(t => Exists(t.Ship) && Transform(t.Ship).MapUid == orbit.Map && !t.Wiped &&
                !InPirateTransit(t.Ship) && PirateAttackAllowed(t, orbit.Map)).ToArray();
            foreach (var pirate in orbit.Pirates)
            {
                if (pirate.Counted) continue;
                var defeated = !Exists(pirate.Grid) || !Exists(pirate.Core) || pirate.Defenders.All(d => !Exists(d) || TryComp<MobStateComponent>(d, out var state) && state.CurrentState == MobState.Dead);
                if (defeated) { pirate.Counted = true; if (Exists(pirate.Core)) { _steering.Stop(pirate.Core); _targeting.Stop(pirate.Core); } _ = RecordPirateDefeat(orbit, pirate); continue; }
                if (targets.Length == 0) { _steering.Stop(pirate.Core); _targeting.Stop(pirate.Core); continue; }
                var target = targets.OrderBy(t => Vector2.DistanceSquared(_transform.GetWorldPosition(t.Ship), _transform.GetWorldPosition(pirate.Grid))).First();
                var coordinates = new EntityCoordinates(target.Ship, Vector2.Zero);
                var steering = _steering.Steer(pirate.Core, coordinates);
                if (steering != null) { steering.NoFinish = true; steering.AlwaysFaceTarget = true; steering.RangeTolerance = 180; steering.AvoidProjectiles = true; }
                _targeting.Target(pirate.Core, coordinates);
            }
        }
    }
    private async Task RecordPirateDefeat(Orbit orbit, PirateShip pirate)
    {
        try
        {
#if DEBUG
            if (orbit.TestEncounter)
            {
                foreach (var trip in _trips.Values.Where(t => t.Sector == orbit.Sector)) trip.CompletedWork++;
                return;
            }
#endif
            var path = _voyages.RouteGraph.Path(VoyageRouteModel.Home, orbit.Sector);
            await _db.SectorEncounterAsync(_voyages.WorldSnapshot.Cycle, orbit.Sector, path, 0, Now, pirate.Encounter, pirate.Index);
            foreach (var trip in _trips.Values.Where(t => Exists(t.Ship) && Transform(t.Ship).MapUid == orbit.Map)) trip.CompletedWork++;
            await _voyages.RefreshGalaxy();
        }
        catch (Exception e) { pirate.Counted = false; Log.Warning($"Pirate defeat persistence deferred: {e.Message}"); }
    }
    private void UnloadEmptyOrbits()
    {
        var protectedMaps = new HashSet<EntityUid>();
        // Protect the actual vessel position even if an administrative move or an
        // exceptional transition has temporarily left its recorded sector behind.
        foreach (var trip in _trips.Values)
            if (!TerminatingOrDeleted(trip.Ship) && Transform(trip.Ship).MapUid is { } actualMap) protectedMaps.Add(actualMap);
        var travellers = AllEntityQuery<Content.Shared.Mind.Components.MindContainerComponent, TransformComponent>();
        while (travellers.MoveNext(out var body, out var mind, out var position))
            if (mind.Mind is { } owner && TryComp<Content.Shared.Mind.MindComponent>(owner, out var identity) && identity.UserId != null &&
                !HasComp<Content.Shared.Ghost.GhostComponent>(body) && position.MapUid is { } map) protectedMaps.Add(map);
        var jumps = AllEntityQuery<Content.Shared.Shuttles.Components.FTLComponent>();
        while (jumps.MoveNext(out var jump))
            if (Exists(jump.TargetCoordinates.EntityId) && _transform.GetMap(jump.TargetCoordinates) is { } map) protectedMaps.Add(map);
        foreach (var orbit in _orbits.Values.ToArray())
        {
            var surfaces = _planetMaps.Where(p => p.Key.Sector == orbit.Sector).ToArray();
            var maps = surfaces.Select(p => p.Value).Append(orbit.Map).ToHashSet();
            if (maps.Overlaps(protectedMaps) || _encounterBusy.Contains(orbit.Sector) ||
                _planetLaunching.Any(p => p.Sector == orbit.Sector) ||
                _trips.Values.Any(t => !t.LocalPlanet && t.Sector == orbit.Sector &&
                    (t.Phase is Phase.Departing or Phase.Orbit or Phase.GeneratingPlanet or Phase.Planet or Phase.LeavingPlanet ||
                     Exists(t.Ship) && Transform(t.Ship).MapUid is { } current && maps.Contains(current)))) continue;
            // Pause immediately, then remove every branch within the existing per-tick deletion budget.
            var deletion = EntityManager.System<Content.Server._DeepLagoon.Performance.StagedMapDeletionSystem>();
            foreach (var surface in surfaces) { _retainedPlanets.Remove(surface.Value); deletion.Queue(surface.Value); _planetMaps.Remove(surface.Key); }
            deletion.Queue(orbit.Map); _orbits.Remove(orbit.Sector);
        }
    }
}

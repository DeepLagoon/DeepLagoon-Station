#if DEBUG
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._DeepLagoon.Mapping;
using Content.Server.Salvage.Expeditions;
using Content.Server.Shuttles.Components;
using Content.Shared._DeepLagoon.Voyages;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Access.Components;
using Content.Shared.GameTicking;
using Content.Shared.NPC.Systems;
using Content.Shared.Salvage;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Voyages;

[CVarDefs]
public sealed class SectorPirateTestCVars
{
    public static readonly CVarDef<bool> Enabled = CVarDef.Create("sector_expeditions.debug_pirate_test", false, CVar.SERVERONLY);
}

public sealed partial class SectorExpeditionSystem
{
    private readonly Dictionary<string, int> _testEncounters = new();
    private readonly HashSet<Guid> _testPilots = new();
    private readonly HashSet<EntityUid> _testShips = new();
    private const string PirateTestBuyer = "Испытательный рейс · один пиратский корабль";

    private void ArmPirateTest(EntityUid ship, SectorResearchOrder order)
    {
        // A restored order can lead to a different sector than the stand prepared on this boot.
        // Training vessels also guarantee a fight when the tester selects an ordinary research order.
        if (!_testShips.Contains(ship) && !(order.DebugFunded && order.Automatic &&
            (order.DebugPirateTest || order.Buyer == PirateTestBuyer))) return;
        _testEncounters[order.Sector] = 1;
        Log.Info($"Pirate test route armed: ship={ship}, order={order.Id}, sector={order.Sector}, pirates=1.");
    }

    private void InitializePirateTestStand()
    {
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(args =>
        {
            if (_config.GetCVar(SectorPirateTestCVars.Enabled)) _ = PreparePirateTest(args.Player, args.Mob);
        });
    }

    /// <summary>Local Debug training setup. Uses normal orders, flight, steering, guns and defenders.</summary>
    public async Task PreparePirateTest(ICommonSession player, EntityUid actor)
    {
        if (!Enabled || !_voyages.Ready || !_testPilots.Add(player.UserId.UserId)) return;
        EntityUid? trainingShip = null;
        string? target = null;
        try
        {
            if (!_bank.TryGetCharacter(player, out _, out _, out var slot)) throw new InvalidOperationException("Нет профиля персонажа");
            var account = await _db.ReadEconomyAccountAsync(player.UserId.UserId, slot);
            if (player.AttachedEntity != actor || Transform(actor).MapUid is not { } home || HasComp<SectorExpeditionMapComponent>(home))
                throw new InvalidOperationException("Стенд создаётся после появления персонажа в родном секторе");
            var graph = _voyages.RouteGraph; var visible = VoyageVisibility.VisibleSystems(graph);
            var sector = graph.Systems.Where(s => visible.Contains(s.Id) && !SectorResearchDefinition.Settled(s.Id) &&
                    !_orbits.ContainsKey(s.Id) && !_testEncounters.ContainsKey(s.Id) && graph.SurveyPath(s.Id).Length > 1 &&
                    (_voyages.WorldSnapshot.Research.GetValueOrDefault(s.Id)?.Completed.Count ?? 0) == 0)
                .OrderBy(s => Vector2.DistanceSquared(s.Position, graph.System(VoyageRouteModel.Home).Position)).FirstOrDefault()
                ?? throw new InvalidOperationException("Нет свободного неисследованного сектора для стенда");
            target = sector.Id;
            var vessel = _prototypes.Index<VesselPrototype>("Kestrel");
            var point = _transform.GetWorldPosition(actor) + new Vector2(450, 250);
            if (!_loader.TryLoadGridTracked(Comp<MapComponent>(home).MapId, vessel.ShuttlePath, out var grid, offset: point))
                throw new InvalidOperationException("Не удалось загрузить учебный боевой шаттл");
            trainingShip = grid.Value.Owner;
            _testShips.Add(trainingShip.Value);
            EntityManager.System<MetaDataSystem>().SetEntityName(trainingShip.Value, "Испытательный Кестрель · бой с пиратами");
            EnsureComp<ShipOwnershipComponent>(trainingShip.Value).OwnerUserId = player.UserId;
            EntityManager.System<NpcFactionSystem>().AddFaction(trainingShip.Value, "NanoTrasen");
            EnsureComp<SalvageExpeditionDataComponent>(trainingShip.Value).Enabled = true;
            var children = AllEntityQuery<TransformComponent>();
            EntityUid? pilot = null;
            while (children.MoveNext(out var uid, out var transform))
            {
                if (transform.GridUid != trainingShip) continue;
                RemComp<AccessReaderComponent>(uid);
                if (HasComp<ShuttleConsoleComponent>(uid)) pilot ??= uid;
            }
            var seats = _maps.GetAllTiles(grid.Value.Owner, grid.Value.Comp)
                .Where(t => !t.Tile.IsEmpty && _anchor.TileFree(grid.Value.Comp, t.GridIndices,
                    (int)Content.Shared.Physics.CollisionGroup.MobLayer, (int)Content.Shared.Physics.CollisionGroup.MobMask))
                .Select(t => _maps.GridTileToLocal(grid.Value.Owner, grid.Value.Comp, t.GridIndices)).ToArray();
            if (seats.Length == 0) throw new InvalidOperationException("На шаттле нет свободного места");
            var bridge = pilot is { } helm ? Transform(helm).Coordinates.Position : Vector2.Zero;
            var seat = seats.OrderBy(c => Vector2.DistanceSquared(c.Position, bridge)).First();
            var console = Spawn("ComputerSalvageExpedition", seat);
            RemComp<AccessReaderComponent>(console);
            RemComp<Content.Server.Power.Components.ApcPowerReceiverComponent>(console);
            _transform.AnchorEntity(console);
            var pilotSeat = seats.Where(c => c != seat).OrderBy(c => Vector2.DistanceSquared(c.Position, seat.Position)).FirstOrDefault();
            _transform.SetCoordinates(actor, pilotSeat.IsValid(EntityManager) ? pilotSeat : seat);
            foreach (var prototype in new[] { "Magazine20mm", "Magazine40mm" })
                if (_prototypes.HasIndex<Robust.Shared.Prototypes.EntityPrototype>(prototype))
                    for (var i = 0; i < 4; i++) Spawn(prototype, seat);
            _testEncounters[sector.Id] = 1;
            var world = _voyages.WorldSnapshot;
            await _db.PublishSectorResearchAsync(new(Guid.NewGuid(), world.Cycle, sector.Id, PirateTestBuyer,
                20000, 20000, Guid.Empty, 0, 0, 0, SectorResearchDefinition.Count(sector.Id, world.Seed), new(), Automatic: true, DebugFunded: true, DebugPirateTest: true));
            if (player.AttachedEntity != actor || !Exists(console)) throw new InvalidOperationException("Персонаж покинул стенд во время подготовки");
            _ui.OpenUi(console, SalvageConsoleUiKey.Expedition, actor);
            await Board(player, console, account, sector.Id, "Испытательный Кестрель · один пиратский корабль · боеприпасы на мостике", focusSelected: true);
            Log.Info($"Pirate test stand ready: actor={actor}, ship={trainingShip}, console={console}, sector={sector.Id}, pirates=1.");
        }
        catch (Exception error)
        {
            _testPilots.Remove(player.UserId.UserId);
            if (trainingShip is { } failedShip) _testShips.Remove(failedShip);
            if (target != null) _testEncounters.Remove(target);
            // A connected pilot is never removed with a partially configured ship.
            if (trainingShip is { } ship && Exists(ship) && Transform(actor).GridUid != ship)
                EntityManager.System<Content.Server._DeepLagoon.Performance.StagedMapDeletionSystem>().Queue(ship);
            Log.Error($"Pirate test stand preparation failed: {error.Message}");
        }
    }
}
#endif

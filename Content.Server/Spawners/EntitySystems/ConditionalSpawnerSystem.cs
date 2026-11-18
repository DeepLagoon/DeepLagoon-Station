using System.Numerics;
using Content.Server.GameTicking;
using Content.Server.Spawners.Components;
using Content.Shared.EntityTable;
using Content.Shared.GameTicking.Components;
using JetBrains.Annotations;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server.Spawners.EntitySystems
{
    [UsedImplicitly]
    public sealed partial class ConditionalSpawnerSystem : EntitySystem
    {
        [Dependency] private IRobustRandom _robustRandom = default!;
        [Dependency] private GameTicker _ticker = default!;
        [Dependency] private EntityTableSystem _entityTable = default!;
        [Dependency] private IPrototypeManager _prototypes = default!;
        [Dependency] private Content.Server._DeepLagoon.Salvage.ExpeditionLootSystem _expeditionLoot = default!;
        private readonly List<EntProtoId> _filteredRare = new(4);

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<GameRuleStartedEvent>(OnRuleStarted);
            SubscribeLocalEvent<ConditionalSpawnerComponent, MapInitEvent>(OnCondSpawnMapInit);
            SubscribeLocalEvent<RandomSpawnerComponent, MapInitEvent>(OnRandSpawnMapInit);
            SubscribeLocalEvent<EntityTableSpawnerComponent, MapInitEvent>(OnEntityTableSpawnMapInit);
        }

        private void OnCondSpawnMapInit(EntityUid uid, ConditionalSpawnerComponent component, MapInitEvent args)
        {
            TrySpawn(uid, component);
        }

        private void OnRandSpawnMapInit(EntityUid uid, RandomSpawnerComponent component, MapInitEvent args)
        {
            Spawn(uid, component);
            if (component.DeleteSpawnerAfterSpawn)
                QueueDel(uid);
        }

        private void OnEntityTableSpawnMapInit(Entity<EntityTableSpawnerComponent> ent, ref MapInitEvent args)
        {
            Spawn(ent);
            if (ent.Comp.DeleteSpawnerAfterSpawn && !TerminatingOrDeleted(ent) && Exists(ent))
                QueueDel(ent);
        }

        public EntProtoId? ResolveBiomeTerrain(EntProtoId prototype, int seed)
        {
            if (_prototypes.TryIndex(prototype, out var entity) &&
                entity.TryComp(out EntityTableSpawnerComponent? spawner, EntityManager.ComponentFactory) &&
                spawner.BiomeTerrain)
            {
                foreach (var terrain in _entityTable.GetSpawns(spawner.Table, new Random(seed)))
                    return terrain;

                return null;
            }

            return prototype;
        }

        private void OnRuleStarted(ref GameRuleStartedEvent args)
        {
            var query = EntityQueryEnumerator<ConditionalSpawnerComponent>();
            while (query.MoveNext(out var uid, out var spawner))
            {
                RuleStarted(uid, spawner, args);
            }
        }

        public void RuleStarted(EntityUid uid, ConditionalSpawnerComponent component, GameRuleStartedEvent obj)
        {
            if (component.GameRules.Contains(obj.RuleId))
                Spawn(uid, component);
        }

        private void TrySpawn(EntityUid uid, ConditionalSpawnerComponent component)
        {
            if (component.GameRules.Count == 0)
            {
                Spawn(uid, component);
                return;
            }

            foreach (var rule in component.GameRules)
            {
                if (!_ticker.IsGameRuleActive(rule))
                    continue;
                Spawn(uid, component);
                return;
            }
        }

        private void Spawn(EntityUid uid, ConditionalSpawnerComponent component)
        {
            if (component.Chance != 1.0f && !_robustRandom.Prob(component.Chance))
                return;

            if (component.Prototypes.Count == 0)
            {
                Log.Warning($"Prototype list in ConditionalSpawnComponent is empty! Entity: {ToPrettyString(uid)}");
                return;
            }

            if (!Deleted(uid))
                _expeditionLoot.SpawnLoot(uid, _robustRandom.Pick(component.Prototypes), Transform(uid).Coordinates);
        }

        private void Spawn(EntityUid uid, RandomSpawnerComponent component)
        {
            SpawnRandom(uid, component, Transform(uid).Coordinates);
        }

        /// <summary>
        /// Resolve a disposable random marker without creating the marker entity. True means the prototype
        /// was handled; a null result is a valid empty roll. Returns the actual output for mining/stack events.
        /// </summary>
        public bool TrySpawnRandom(EntityUid source, EntProtoId prototype, EntityCoordinates coordinates, out EntityUid? spawned)
        {
            spawned = null;
            if (!_prototypes.TryIndex(prototype, out var entity)
                || !entity.TryGetComponent<RandomSpawnerComponent>(out var spawner, EntityManager.ComponentFactory)
                || !spawner.DeleteSpawnerAfterSpawn) return false;
            // Preserve the origin/caps a fresh marker at the destination would have acquired. The miner
            // itself may have retained a different origin after parking or travel between expeditions.
            _expeditionLoot.TryGetSpawnOrigin(coordinates, out var origin);
            spawned = SpawnRandom(source, spawner, coordinates, origin);
            return true;
        }

        private EntityUid? SpawnRandom(EntityUid uid, RandomSpawnerComponent component, EntityCoordinates coordinates)
        {
            _expeditionLoot.TryGetOrigin(uid, out var origin);
            return SpawnRandom(uid, component, coordinates, origin);
        }

        private EntityUid? SpawnRandom(EntityUid uid, RandomSpawnerComponent component, EntityCoordinates coordinates,
            Content.Server._DeepLagoon.Salvage.ExpeditionLootOriginComponent? origin)
        {
            // Do not reroll a capped T3 marker through its T4 -> T3 replacement forever.
            IReadOnlyList<EntProtoId> rare = component.RarePrototypes;
            if (origin != null)
            {
                _filteredRare.Clear();
                foreach (var id in component.RarePrototypes)
                    if (_expeditionLoot.GetTier(id) <= origin.MaximumTier) _filteredRare.Add(id);
                rare = _filteredRare;
            }
            if (rare.Count > 0 && (component.RareChance == 1.0f || _robustRandom.Prob(component.RareChance)))
            {
                // Finish selection before spawning: a nested random marker can reuse the same buffer.
                var selected = _robustRandom.Pick(rare);
                return _expeditionLoot.SpawnResolvedLoot(selected, coordinates, origin);
            }

            if (component.Chance != 1.0f && !_robustRandom.Prob(component.Chance))
                return null;

            if (component.Prototypes.Count == 0)
            {
                Log.Warning($"Prototype list in RandomSpawnerComponent is empty! Entity: {ToPrettyString(uid)}");
                return null;
            }

            if (Deleted(uid))
                return null;

            var offset = component.Offset;
            var xOffset = _robustRandom.NextFloat(-offset, offset);
            var yOffset = _robustRandom.NextFloat(-offset, offset);

            coordinates = coordinates.Offset(new Vector2(xOffset, yOffset));

            return _expeditionLoot.SpawnResolvedLoot(_robustRandom.Pick(component.Prototypes), coordinates, origin);
        }

        private void Spawn(Entity<EntityTableSpawnerComponent> ent)
        {
            if (TerminatingOrDeleted(ent) || !Exists(ent))
                return;

            var coords = Transform(ent).Coordinates;

            var spawns = _entityTable.GetSpawns(ent.Comp.Table);
            foreach (var proto in spawns)
            {
                var xOffset = _robustRandom.NextFloat(-ent.Comp.Offset, ent.Comp.Offset);
                var yOffset = _robustRandom.NextFloat(-ent.Comp.Offset, ent.Comp.Offset);
                var trueCoords = coords.Offset(new Vector2(xOffset, yOffset));

                _expeditionLoot.SpawnLoot(ent, proto, trueCoords);
            }
        }
    }
}

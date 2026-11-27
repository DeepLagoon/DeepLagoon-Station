using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.Research.Components;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingSystem
{
#if DEEPLAGOON_ENGINE_INCREMENTAL_SNAPSHOTS
    private IEnumerable<MappingDataNode?> PrepareFreeParkingShip(EntityUid grid, ParkingShuttleComponent ship,
        HashSet<EntityUid> inside, EntitySerializer context)
    {
        var serialization = _dependencies.Resolve<ISerializationManager>();
        var expeditions = _salvage.GridExpeditionSettings(grid);
        var header = new ParkingShuttleComponent
        {
            Identity = ship.Identity, OwnerAccount = ship.OwnerAccount, OriginalPrice = ship.OriginalPrice,
            Vessel = ship.Vessel, PurchasedWithVoucher = ship.PurchasedWithVoucher, OriginFaction = ship.OriginFaction, OwnerName = ship.OwnerName, Parked = true, EquipmentBaselineRecorded = ship.EquipmentBaselineRecorded,
            OriginalShieldPrototype = ship.OriginalShieldPrototype, ExpeditionsEnabled = expeditions.Enabled,
            ParkingProfile = ship.ParkingProfile, ParkingCharacter = ship.ParkingCharacter,
            ExpeditionCooldown = expeditions.Cooldown, ExpeditionNextOffer = expeditions.NextOffer,
        };
        var node = serialization.WriteValueAs<MappingDataNode>(typeof(ParkingShuttleComponent), header, alwaysWrite: true, context: context);
        var originals = new MappingDataNode(); node["originalItems"] = originals;
        foreach (var (identity, entity) in ship.OriginalItems)
        {
            if (inside.Contains(entity)) originals.Add((ValueDataNode)serialization.WriteValue(identity, context: context), serialization.WriteValue(entity, context: context));
            yield return null;
        }
        var connections = new MappingDataNode(); node["researchConnections"] = connections;
        foreach (var entity in inside)
        {
            if (HasComp<ResearchClientComponent>(entity))
            {
                var server = _research.TryGetClientServer(entity, out var target, out _) && inside.Contains(target.Value) ? target : null;
                connections.Add((ValueDataNode)serialization.WriteValue(entity, context: context), serialization.WriteValue(server, context: context));
            }
            yield return null;
        }
        yield return node;
    }

    private IEnumerable<MappingDataNode?> PrepareFreeParkingRepair(ShipRepairDataComponent blueprint,
        HashSet<EntityUid> inside, EntitySerializer context)
    {
        var serialization = _dependencies.Resolve<ISerializationManager>();
        var header = new ShipRepairDataComponent
        {
            ChunkSize = blueprint.ChunkSize, EntityPalette = blueprint.EntityPalette,
            SnapshotReadyAt = blueprint.SnapshotReadyAt, SnapshotVersion = blueprint.SnapshotVersion,
        };
        var node = serialization.WriteValueAs<MappingDataNode>(typeof(ShipRepairDataComponent), header, alwaysWrite: true, context: context);
        var chunks = new MappingDataNode(); node["chunks"] = chunks;
        foreach (var (indices, chunk) in blueprint.Chunks)
        {
            var chunkNode = serialization.WriteValueAs<MappingDataNode>(typeof(ShipRepairChunk), new ShipRepairChunk { Tiles = chunk.Tiles, NextUid = chunk.NextUid },
                alwaysWrite: true, context: context);
            var entities = new MappingDataNode(); chunkNode["entities"] = entities;
            foreach (var (id, source) in chunk.Entities)
            {
                var original = source.OriginalEntity is { } net && TryGetEntity(net, out var uid) && uid is { } entity && inside.Contains(entity)
                    ? source.OriginalEntity : null;
                var specifier = new ShipRepairEntitySpecifier
                {
                    ProtoIndex = source.ProtoIndex, LocalPosition = source.LocalPosition, Rotation = source.Rotation,
                    OriginalEntity = original, ParkingOriginalIdentity = source.ParkingOriginalIdentity,
                    ParkingBaseline = source.ParkingBaseline, PaidCapital = source.PaidCapital,
                };
                entities.Add(new ValueDataNode(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    serialization.WriteValue(specifier, alwaysWrite: true, context: context, notNullableOverride: true));
                yield return null;
            }
            chunks.Add((ValueDataNode)serialization.WriteValue(indices, context: context), chunkNode);
            yield return null;
        }
        yield return node;
    }
#endif
}

using Robust.Shared.Prototypes;
using Content.Shared.Parallax.Biomes.Markers;

namespace Content.Shared.Procedural.Loot;

/// <summary>
/// Adds a biome marker layer for dungeon loot.
/// </summary>
public sealed partial class BiomeMarkerLoot : IDungeonLoot
{
    [DataField("proto", required: true)]
    public Dictionary<string, ProtoId<BiomeMarkerLayerPrototype>> Prototype = new();
}

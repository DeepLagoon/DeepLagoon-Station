using System.Globalization;
using System.IO;
using System.Linq;
using Content.Shared._DeepLagoon.Mapping;
using YamlDotNet.RepresentationModel;

namespace Content.Client._Mono.Shipyard;

/// <summary>Reads only display data from a shuttle file, without deserializing components.</summary>
public sealed class ShipyardPreviewScene
{
    public readonly List<GridEditorItem> Items = new();
    public int X, Y, Width, Height;
    private static YamlNode? Field(YamlMappingNode node, string key) =>
        node.FirstOrDefault(pair => pair.Key is YamlScalarNode scalar && scalar.Value == key).Value;
    private static string Text(YamlMappingNode node, string key, string fallback = "") => (Field(node, key) as YamlScalarNode)?.Value ?? fallback;
    private static YamlMappingNode? Component(YamlMappingNode entity, string type) =>
        (Field(entity, "components") as YamlSequenceNode)?.Children.OfType<YamlMappingNode>().FirstOrDefault(node => Text(node, "type") == type);
    private static (float X, float Y) Position(string value)
    {
        var parts = value.Split(',');
        return (float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture));
    }
    private static float Rotation(string value) => value.EndsWith("rad", StringComparison.Ordinal)
        ? float.Parse(value[..^3], CultureInfo.InvariantCulture) * 180 / MathF.PI
        : float.Parse(value.Replace("deg", ""), CultureInfo.InvariantCulture);

    public static ShipyardPreviewScene Read(TextReader reader)
    {
        var yaml = new YamlStream(); yaml.Load(reader);
        var root = (YamlMappingNode) yaml.Documents.Single().RootNode;
        var grids = (YamlSequenceNode) root["grids"];
        if (grids.Children.Count != 1) throw new InvalidDataException("A vessel preview requires one grid.");
        var gridId = ((YamlScalarNode) grids.Children[0]).Value!;
        var scene = new ShipyardPreviewScene();
        var tileMap = ((YamlMappingNode) root["tilemap"]).ToDictionary(pair => int.Parse(((YamlScalarNode) pair.Key).Value!, CultureInfo.InvariantCulture), pair => ((YamlScalarNode) pair.Value).Value!);
        foreach (var group in ((YamlSequenceNode) root["entities"]).Children.Cast<YamlMappingNode>())
        foreach (var entity in ((YamlSequenceNode) group["entities"]).Children.Cast<YamlMappingNode>())
        {
            var id = Text(entity, "uid");
            if (id == gridId)
            {
                if (Component(entity, "MapGrid") is { } grid) scene.ReadTiles(grid, tileMap);
                if (Component(entity, "DecalGrid") is { } decals) scene.ReadDecals(decals);
                continue;
            }
            if (Component(entity, "Transform") is not { } transform || Text(transform, "parent") != gridId) continue;
            var proto = Text(group, "proto");
            if (proto.Length == 0) continue;
            var (x, y) = Position(Text(transform, "pos", "0,0"));
            scene.Items.Add(new GridEditorItem { Id = "e:" + id, Kind = "entity", Prototype = proto, X = x, Y = y,
                Rotation = Rotation(Text(transform, "rot", "0")), Color = Component(entity, "Sprite") is { } sprite ? Text(sprite, "color", "#FFFFFFFF") : "#FFFFFFFF" });
        }
        if (scene.Items.Count == 0) throw new InvalidDataException("Empty vessel preview.");
        scene.X = (int) MathF.Floor(scene.Items.Min(item => item.X)) - 1;
        scene.Y = (int) MathF.Floor(scene.Items.Min(item => item.Y)) - 1;
        scene.Width = (int) MathF.Ceiling(scene.Items.Max(item => item.X)) - scene.X + 2;
        scene.Height = (int) MathF.Ceiling(scene.Items.Max(item => item.Y)) - scene.Y + 2;
        return scene;
    }

    private void ReadTiles(YamlMappingNode grid, Dictionary<int, string> tileMap)
    {
        if (Field(grid, "chunks") is not YamlMappingNode chunks) return;
        foreach (var pair in chunks)
        {
            var chunk = (YamlMappingNode) pair.Value;
            var (cx, cy) = Position(Text(chunk, "ind"));
            var size = int.Parse(Text(chunk, "size", "16"), CultureInfo.InvariantCulture);
            var version = int.Parse(Text(chunk, "version", "1"), CultureInfo.InvariantCulture);
            using var stream = new MemoryStream(Convert.FromBase64String(Text(chunk, "tiles")));
            using var binary = new BinaryReader(stream);
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var id = version < 6 ? binary.ReadUInt16() : binary.ReadInt32();
                var flags = binary.ReadByte(); var variant = binary.ReadByte();
                var mirror = version >= 7 ? binary.ReadByte() : (byte) 0;
                if (!tileMap.TryGetValue(id, out var proto) || id == 0 || proto == "Space") continue;
                Items.Add(new GridEditorItem { Id = $"t:{cx * size + x}:{cy * size + y}", Kind = "tile", Prototype = proto,
                    X = cx * size + x, Y = cy * size + y, Flags = flags, Variant = variant, Mirror = mirror });
            }
        }
    }

    private void ReadDecals(YamlMappingNode component)
    {
        if (Field(component, "chunkCollection") is not YamlMappingNode collection || Field(collection, "nodes") is not YamlSequenceNode nodes) return;
        foreach (var entry in nodes.Children.Cast<YamlMappingNode>())
        {
            var node = (YamlMappingNode) entry["node"];
            foreach (var pair in (YamlMappingNode) entry["decals"])
            {
                var (x, y) = Position(((YamlScalarNode) pair.Value).Value!);
                Items.Add(new GridEditorItem { Id = "d:" + ((YamlScalarNode) pair.Key).Value, Kind = "decal", Prototype = Text(node, "id"),
                    X = x, Y = y, Rotation = Rotation(Text(node, "angle", "0")), Color = Text(node, "color", "#FFFFFFFF"),
                    Z = int.Parse(Text(node, "zIndex", "0"), CultureInfo.InvariantCulture) });
            }
        }
    }
}

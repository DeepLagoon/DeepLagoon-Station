using System.Globalization;
using System.IO;
using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Mapping;
using Robust.Shared.ContentPack;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Utility;
using YamlDotNet.RepresentationModel;

namespace Content.Client._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{
    [Dependency] private IResourceManager _resources = default!;
    private const string LibraryFile = "/grid-editor/library.json";
    private readonly Dictionary<string, (string Kind, string Prototype)> _favorites = new();
    private readonly Dictionary<int, (string Kind, string Prototype)> _hotbar = new();
    private readonly Dictionary<string, (string Name, List<GridEditorItem> Items)> _templates = new();
    private bool _libraryLoaded;

    private static string Field(MappingDataNode node, string key, string fallback = "") =>
        node.TryGet<ValueDataNode>(key, out var value) ? value.Value : fallback;

    private static List<GridEditorItem> ReadItems(SequenceDataNode sequence)
    {
        var items = new List<GridEditorItem>();
        foreach (var child in sequence)
        {
            if (child is not MappingDataNode node) throw new InvalidDataException();
            float Number(string key) => float.Parse(Field(node, key, "0"), CultureInfo.InvariantCulture);
            items.Add(new GridEditorItem { Id = Field(node, "id"), Kind = Field(node, "kind"), Prototype = Field(node, "prototype"),
                X = Number("x"), Y = Number("y"), Rotation = Number("rotation"), Z = (int) Number("z"), Color = Field(node, "color", "#FFFFFFFF"),
                Cleanable = Field(node, "cleanable") == "true", Anchored = Field(node, "anchored") == "true",
                Variant = CheckedByte(Number("variant")), Flags = CheckedByte(Number("flags")), Mirror = CheckedByte(Number("mirror")) });
        }
        return items;
    }

    private void LoadLibrary()
    {
        if (_libraryLoaded) return;
        _libraryLoaded = true;
        _hotbar.Clear();
        var path = new ResPath(LibraryFile);
        try
        {
            if (!_resources.UserData.Exists(path)) return;
            using var reader = _resources.UserData.OpenText(path);
            var text = reader.ReadToEnd();
            if (text.Length > 4_000_000) throw new InvalidDataException("Library too large");
            var yaml = new YamlStream(); yaml.Load(new StringReader(text));
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode.ToDataNode() is not MappingDataNode root || Field(root, "version") != "1")
                throw new InvalidDataException("Unsupported library");
            if (root.TryGet<SequenceDataNode>("favorites", out var favorites) && favorites.Count <= 512)
                foreach (var child in favorites)
                    if (child is MappingDataNode node)
                    {
                        var kind = Field(node, "kind"); var id = Field(node, "id");
                        if (kind is "entity" or "tile" or "decal" && id.Length is > 0 and <= 200) _favorites[kind + ":" + id] = (kind, id);
                    }
            if (root.TryGet<SequenceDataNode>("templates", out var templates) && templates.Count <= 64)
                foreach (var child in templates)
                    if (child is MappingDataNode node && Field(node, "id") is { Length: > 0 and <= 64 } id &&
                        Field(node, "name") is { Length: > 0 and <= 64 } name && node.TryGet<SequenceDataNode>("items", out var items) && items.Count is > 0 and <= 512)
                        _templates[id] = (name, ReadItems(items));
            if (Field(root, "hotbarVersion") == "2" && root.TryGet<SequenceDataNode>("hotbar", out var hotbar))
            {
                foreach (var child in hotbar)
                    if (child is MappingDataNode node && int.TryParse(Field(node, "slot"), out var slot) && slot >= 0)
                    {
                        var kind = Field(node, "kind"); var id = Field(node, "id");
                        if (kind is "entity" or "tile" or "decal" && id.Length is > 0 and <= 200) _hotbar[slot] = (kind, id);
                    }
            }
        }
        catch (Exception e) { Log.Warning($"Unable to load grid editor library: {e.Message}"); Status("Не удалось прочитать библиотеку. Исходный файл сохранён.", false); }
    }

    private TguiData LibraryData() => new TguiData().Number("version", 1)
        .Number("hotbarVersion", 2)
        .Array("hotbar", HotbarData())
        .Array("favorites", _favorites.Values.Select(f => new TguiData().String("kind", f.Kind).String("id", f.Prototype)))
        .Array("templates", _templates.Select(t => new TguiData().String("id", t.Key).String("name", t.Value.Name)
            .Array("items", t.Value.Items.Select(ItemData))));

    private IEnumerable<TguiData> HotbarData() => _hotbar.OrderBy(entry => entry.Key).Select(entry => new TguiData()
        .Number("slot", entry.Key).String("kind", entry.Value.Kind).String("id", entry.Value.Prototype));

    private void PersistLibrary()
    {
        var content = LibraryData().ToString();
        if (content.Length > 4_000_000) throw new InvalidDataException("Библиотека превышает 4 МБ.");
        var data = _resources.UserData;
        var path = new ResPath(LibraryFile); var temporary = new ResPath(LibraryFile + ".tmp"); var backup = new ResPath(LibraryFile + ".bak");
        data.CreateDir(new ResPath("/grid-editor"));
        using (var writer = data.OpenWriteText(temporary)) writer.Write(content);
        if (data.Exists(backup)) data.Delete(backup);
        if (data.Exists(path)) data.Rename(path, backup);
        try { data.Rename(temporary, path); }
        catch { if (!data.Exists(path) && data.Exists(backup)) data.Rename(backup, path); throw; }
    }

    private void PublishLibrary()
    {
        foreach (var token in _tabs.Keys.ToArray()) InTab(token, PublishTabLibrary);
    }

    private void PublishTabLibrary()
    {
        var available = _templates.Select(t => new TguiData().String("id", t.Key).String("name", t.Value.Name)
            .Array("items", t.Value.Items.Select(ItemData))).ToList();
        var visuals = _templates.Values.SelectMany(t => t.Items).Select(i => (i.Kind, i.Prototype))
            .Concat(_favorites.Values.Select(f => (f.Kind, f.Prototype))).Concat(_hotbar.Values).Distinct().Select(i =>
            {
                try { return Visual(i.Kind, i.Prototype); }
                catch (Exception) { return new TguiData().String("kind", i.Kind).String("id", i.Prototype).Bool("missing", true); }
            });
        SendTabUpdate(new TguiData().Object("data", new TguiData()
            .Array("hotbar", HotbarData())
            .Array("favorites", _favorites.Values.Select(f => new TguiData().String("kind", f.Kind).String("id", f.Prototype)))
            .Array("templates", available).Array("libraryVisuals", visuals)).ToString());
    }

    private bool LibraryAction(string action, MappingDataNode root)
    {
        if (action == "hotbarSet")
        {
            if (!int.TryParse(Field(root, "slot"), out var slot) || slot < 0) return true;
            var kind = Field(root, "kind"); var id = Field(root, "id");
            if (id.Length == 0) _hotbar.Remove(slot);
            else
            {
                if (kind is not ("entity" or "tile" or "decal") || id.Length > 200) return true;
                _ = Visual(kind, id);
                _hotbar[slot] = (kind, id);
            }
            PersistLibrary(); PublishLibrary(); return true;
        }
        if (action == "favorite")
        {
            var kind = Field(root, "kind"); var id = Field(root, "id");
            if (kind is not ("entity" or "tile" or "decal") || id.Length is < 1 or > 200) return true;
            var key = kind + ":" + id;
            if (!_favorites.Remove(key)) { if (_favorites.Count >= 512) throw new InvalidDataException("Не более 512 избранных элементов."); _favorites[key] = (kind, id); }
            PersistLibrary(); PublishLibrary();
            return true;
        }
        if (action == "deleteTemplate")
        {
            _templates.Remove(Field(root, "id")); PersistLibrary(); PublishLibrary(); return true;
        }
        return false;
    }

    private void SaveTemplate(string name, List<GridEditorItem> items)
    {
        if (_templates.Count >= 64) throw new InvalidDataException("Не более 64 шаблонов.");
        _templates[Guid.NewGuid().ToString()] = (name, items);
        PersistLibrary(); PublishLibrary(); Status("Шаблон сохранён в библиотеке на этом компьютере.", true);
    }

    private void Status(string message, bool success) => SendTabUpdate(new TguiData().Object("data",
        new TguiData().String("status", message).Bool("success", success).Number("resultVersion", ++_resultVersion)).ToString());
}

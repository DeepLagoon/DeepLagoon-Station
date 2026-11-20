using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.Server._DeepLagoon.Apartments;

/// <summary>
/// Transfers exclusive ownership of a completed native DataNode snapshot. Contains no ECS,
/// serializer context, prototype service or deferred entity enumeration. The caller must not
/// access the node graph after handoff; both saves are derived from this one capture.
/// </summary>
public sealed class ApartmentSnapshotCapture
{
    private MappingDataNode? _data;
    private readonly HashSet<string> _removedIds;
    private readonly HashSet<string> _trashBinIds;
    private readonly Lazy<FormattedApartmentGrid> _formatted;

    public ApartmentSnapshotCapture(MappingDataNode ownedData, IEnumerable<string> removedIds, IEnumerable<string> trashBinIds)
    {
        _data = ownedData;
        _removedIds = new(removedIds, StringComparer.Ordinal);
        _trashBinIds = new(trashBinIds, StringComparer.Ordinal);
        _formatted = new(FormatCore, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal FormattedApartmentGrid Format() => _formatted.Value;

    private FormattedApartmentGrid FormatCore()
    {
        if (SynchronizationContext.Current?.GetType().FullName == "Robust.Shared.Asynchronous.RobustSynchronizationContext")
            throw new InvalidOperationException("Apartment YAML formatting must not run on the game thread.");
        var data = _data ?? throw new InvalidOperationException("Apartment snapshot graph is unavailable.");
        var started = Stopwatch.GetTimestamp();
        var stage = "yaml_worker";
        try
        {
            var current = Write(data);
            var currentDone = Stopwatch.GetTimestamp();
            if (_removedIds.Count == 0 && _trashBinIds.Count == 0)
                return new(current, current, started, Stopwatch.GetElapsedTime(started, currentDone), TimeSpan.Zero, TimeSpan.Zero);

            stage = "round_end_build_worker";
            var count = 0;
            foreach (var group in data.Get<SequenceDataNode>("entities").Cast<MappingDataNode>())
            {
                var entries = group.Get<SequenceDataNode>("entities");
                foreach (var entry in entries.Cast<MappingDataNode>().ToArray())
                {
                    var id = entry.Get<ValueDataNode>("uid").Value;
                    if (_removedIds.Contains(id)) { entries.Remove(entry); continue; }
                    if (!_trashBinIds.Contains(id) || !entry.TryGet<SequenceDataNode>("components", out var components)) continue;
                    foreach (var component in components.Cast<MappingDataNode>())
                    {
                        var type = component.Get<ValueDataNode>("type").Value;
                        if (type == "Storage") component["storedItems"] = new MappingDataNode();
                        if (type != "ContainerContainer" || !component.TryGet<MappingDataNode>("containers", out var containers)) continue;
                        foreach (var container in containers.Values.Cast<MappingDataNode>()) container["ents"] = new SequenceDataNode();
                    }
                }
                count += entries.Count;
            }
            data.Get<MappingDataNode>("meta")["entityCount"] = new ValueDataNode(count.ToString(CultureInfo.InvariantCulture));
            foreach (var field in new[] { "orphans", "nullspace" })
            foreach (var entry in data.Get<SequenceDataNode>(field).Cast<ValueDataNode>().ToArray())
                if (_removedIds.Contains(entry.Value)) data.Get<SequenceDataNode>(field).Remove(entry);

            var cleanBuilt = Stopwatch.GetTimestamp();
            stage = "round_end_yaml_worker";
            var clean = Write(data);
            return new(current, clean, started, Stopwatch.GetElapsedTime(started, currentDone),
                Stopwatch.GetElapsedTime(currentDone, cleanBuilt), Stopwatch.GetElapsedTime(cleanBuilt));
        }
        catch (Exception error) { error.Data["ApartmentSaveStage"] = stage; throw; }
        finally
        {
            // The queued request may outlive formatting while SQLite is busy. Keep the two
            // immutable strings, not the much larger temporary node graph, during that wait.
            _data = null;
            _removedIds.Clear(); _trashBinIds.Clear();
        }
    }

    private static string Write(MappingDataNode data)
    {
        using var writer = new StringWriter();
        new YamlStream { new YamlDocument(data.ToYaml()) }.Save(new YamlMappingFix(new Emitter(writer)), false);
        return writer.ToString();
    }

    internal static (ApartmentSnapshot Current, ApartmentSnapshot RoundEnd) Attach(FormattedApartmentGrid grid, ApartmentSnapshot metadata)
    {
        metadata.Grid = grid.Current;
        return (metadata, ReferenceEquals(grid.Current, grid.RoundEnd) ? metadata : new ApartmentSnapshot
        { Grid = grid.RoundEnd, Layout = metadata.Layout, MapFurniture = metadata.MapFurniture });
    }
}

internal readonly record struct FormattedApartmentGrid(string Current, string RoundEnd, long Started,
    TimeSpan Yaml, TimeSpan RoundEndBuild, TimeSpan RoundEndYaml);

public readonly record struct ApartmentSnapshotWriteResult(int YamlChars, long WorkerStarted,
    TimeSpan Yaml, TimeSpan RoundEndBuild, TimeSpan RoundEndYaml, TimeSpan Storage);

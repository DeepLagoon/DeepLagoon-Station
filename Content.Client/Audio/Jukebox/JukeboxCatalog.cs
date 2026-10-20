using System.Linq;

namespace Content.Client.Audio.Jukebox;

/// <summary>Metadata only. Never load audio to browse the catalog.</summary>
public sealed class JukeboxCatalog
{
    public const int PageSize = 25;
    public sealed record Track(string Id, string Name);
    public sealed record Row(string Id, string Track, int SourceIndex);
    public sealed record Page(Row[] Rows, int Total, int SourceCount, int Index);
    private readonly Track[] _tracks;
    private readonly Dictionary<string, Track> _byId;
    public int Count => _tracks.Length;
    public string Name(string id) => _byId.TryGetValue(id, out var track) ? track.Name : id;

    public JukeboxCatalog(Track[] tracks)
    {
        _tracks = tracks.OrderBy(t => t.Name, StringComparer.CurrentCulture).ThenBy(t => t.Id, StringComparer.Ordinal).ToArray();
        _byId = tracks.ToDictionary(t => t.Id);
    }

    // Run on a worker: only immutable snapshots are read here. One bounded page
    // crosses the browser bridge even when the catalog has hundreds of thousands of tracks.
    public Page Browse(Row[]? source, string query, int page)
    {
        var count = source?.Length ?? _tracks.Length;
        if (query.Length == 0)
        {
            var index = Math.Clamp(page, 0, Math.Max(0, (count - 1) / PageSize));
            var offset = index * PageSize;
            var size = Math.Min(PageSize, count - offset);
            var visible = new Row[size];
            for (var i = 0; i < size; i++)
                visible[i] = source == null ? new Row(_tracks[offset + i].Id, _tracks[offset + i].Id, offset + i) : source[offset + i];
            return new Page(visible, count, count, index);
        }
        var rows = new List<Row>(PageSize);
        var total = 0;
        var start = (long) Math.Max(0, page) * PageSize;
        for (var i = 0; i < count; i++)
        {
            var id = source == null ? _tracks[i].Id : source[i].Track;
            var name = _byId.TryGetValue(id, out var track) ? track.Name : id;
            if (!name.Contains(query, StringComparison.CurrentCultureIgnoreCase)) continue;
            if (total >= start && rows.Count < PageSize)
                rows.Add(source == null ? new Row(id, id, i) : source[i]);
            total++;
        }
        var last = Math.Max(0, (total - 1) / PageSize);
        return page > last ? Browse(source, query, last) : new Page(rows.ToArray(), total, count, Math.Max(0, page));
    }

    public string? RandomTrack() => _tracks.Length == 0 ? null : _tracks[Random.Shared.Next(_tracks.Length)].Id;
}

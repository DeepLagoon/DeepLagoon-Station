using System.Linq;
using System.Threading.Tasks;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared.Audio.Jukebox;

namespace Content.Client.Audio.Jukebox;

public sealed partial class JukeboxBoundUserInterface
{
    private Task<(JukeboxCatalog Catalog, JukeboxCatalog.Page Page)>? _browse;
    private JukeboxCatalog? _catalog;
    private JukeboxCatalog.Page _page = new([], 0, 0, 0);
    private string _catalogTab = "catalog", _catalogQuery = "", _catalogPlaylist = "";
    private string _pageTab = "catalog", _browsingTab = "catalog";
    private int _catalogPage;
    private bool _browseDirty;
    private readonly HashSet<string> _metadataRequested = new();

    private void BrowseCatalog()
    {
        if (_tguiWindow == null) return;
        if (_browse != null) { _browseDirty = true; return; }
        var catalog = EntMan.System<JukeboxSystem>().Catalog;
        JukeboxCatalog.Row[]? source = _catalogTab switch
        {
            "queue" => _queue.Queue.Select((e, i) => new JukeboxCatalog.Row(e.Id, e.Track, i)).ToArray(),
            "favorites" => _library.Favorites.Select((id, i) => new JukeboxCatalog.Row(id, id, i)).ToArray(),
            "playlists" => _library.Playlists.GetValueOrDefault(_catalogPlaylist, [])
                .Select((id, i) => new JukeboxCatalog.Row(id, id, i)).ToArray(),
            _ => null,
        };
        var query = _catalogTab == "queue" ? "" : _catalogQuery;
        _browsingTab = _catalogTab;
        var page = _catalogPage;
        _browse = Task.Run(async () =>
        {
            var index = await catalog;
            return (index, index.Browse(source, query, page));
        });
    }

    private void PollCatalog()
    {
        if (_browse is not { IsCompleted: true } task) return;
        _browse = null;
        // A changed search or source supersedes this snapshot. Never publish stale rows.
        if (_browseDirty) { _browseDirty = false; BrowseCatalog(); return; }
        (_catalog, _page) = task.GetAwaiter().GetResult();
        _pageTab = _browsingTab;
        _catalogPage = _page.Index;
        RequestMetadata();
    }

    private void RequestMetadata()
    {
        if (_tguiWindow == null) return;
        var ids = _page.Rows.Select(e => e.Track).ToList();
        if (EntMan.TryGetComponent<JukeboxComponent>(Owner, out var component) && component.SelectedSongId is { } selected)
            ids.Add(selected.Id);
        var missing = ids.Distinct().Where(id => _protoManager.HasIndex<JukeboxPrototype>(id) && _metadataRequested.Add(id)).ToArray();
        if (missing.Length > 0) SendMessage(new JukeboxMetadataRequest(missing));
    }

    private TguiData TrackData(string id)
    {
        var available = _protoManager.TryIndex<JukeboxPrototype>(id, out var track);
        return new TguiData().String("id", id).String("name", available ? track!.Name : id)
            .Bool("available", available).Number("length", _trackLengths.GetValueOrDefault(id));
    }
}

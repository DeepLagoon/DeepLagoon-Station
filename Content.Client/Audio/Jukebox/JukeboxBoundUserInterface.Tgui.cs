using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared.Audio.Jukebox;
using Robust.Client.UserInterface;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client.Audio.Jukebox;

public sealed partial class JukeboxBoundUserInterface
{
    private JukeboxTguiWindow? _tguiWindow;
    private bool _classic;
    private float _refresh, _readyWait;
    private JukeboxLibrary _library = new();
    private bool _libraryLoaded, _libraryBusy;
    private string _libraryStatus = "";
    private JukeboxState _queue = new([], JukeboxRepeat.Off, 0, true);
    private readonly Dictionary<string, float> _trackLengths = new();
    private static readonly string[] Labels = ["title", "playing", "paused", "stopped", "no-track", "catalog", "favorites", "queue", "playlists", "search", "volume", "play", "pause", "stop", "next", "restart", "repeat-off", "repeat-track", "repeat-queue", "random", "add", "add-first", "play-now", "favorite", "up", "down", "remove", "clear", "empty", "unavailable", "new", "rename", "delete", "playlist-name", "create", "save", "cancel", "queue-playlist", "add-playlist", "remove-playlist", "manage", "import", "export", "import-hint", "confirm-delete", "confirm-clear", "close", "loading", "no-power", "visualizer", "visualizer-hint", "saved", "save-error", "invalid", "busy", "queued", "queue-full", "reload", "tracks", "resize"];

    private bool OpenTgui()
    {
        if (_classic || !WindowInterfaceStyle.UseTgui("Jukebox")) return false;
        _readyWait = _refresh = 0;
        _tguiWindow = new JukeboxTguiWindow();
        _tguiWindow.OnClose += Close;
        WindowInterfaceStyle.OpenPresentation(this, _tguiWindow, _menu);
        _tguiWindow.Panel.OnClose += Close;
        _tguiWindow.Panel.OnAction += OnTguiAction;
        _tguiWindow.Frame += OnTguiFrame;
        _tguiWindow.Panel.Web.Failed += DeferClassic;
        _tguiWindow.Panel.BindStyle(_tguiWindow, "Jukebox", () => true, SwitchInterface);
        _tguiWindow.Panel.SetNativeFallback(DeferClassic);
        PopulateTguiCatalog();
        _libraryBusy = true;
        PublishTgui();
        SendMessage(new JukeboxLibraryRequest(JukeboxLibraryAction.Load));
        return true;
    }

    private void DeferClassic() => IoCManager.Resolve<Robust.Client.UserInterface.IUserInterfaceManager>().DeferAction(() =>
    {
        if (_tguiWindow == null || !IsOpened) return;
        _classic = true;
        CloseTgui();
        Open();
    });

    private void CloseTgui()
    {
        var window = _tguiWindow;
        if (window == null) return;
        _tguiWindow = null;
        window.Frame -= OnTguiFrame;
        window.Panel.OnClose -= Close;
        window.Panel.OnAction -= OnTguiAction;
        window.Panel.Web.Failed -= DeferClassic;
        // Detach the BUI close callback while replacing its presentation.
        WindowInterfaceStyle.ClosePresentation(this, window);
        // Leaving the UI tree closes the browser. CEF may still call GetViewRect
        // until OnBeforeClose; disposing the control makes the signed WebView
        // return an empty rectangle, which aborts the entire client process.
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) CloseTgui();
        base.Dispose(disposing);
    }

    private void OnTguiFrame(float seconds)
    {
        if (_tguiWindow == null) return;
        if (!_tguiWindow.Panel.Web.IsReady && (_readyWait += seconds) > 8) { DeferClassic(); return; }
        if ((_refresh += seconds) < 0.25f) return;
        _refresh = 0;
        PollCatalog();
        RequestMetadata();
        PublishTgui();
    }

    private void PopulateTguiCatalog()
    {
        if (_tguiWindow == null) return;
        _trackLengths.Clear();
        _metadataRequested.Clear();
        BrowseCatalog();
        var labels = new TguiData();
        foreach (var key in Labels.Concat(new[] { "seek", "position" })) labels.String(key, Loc.GetString("dl-jukebox-" + key));
        _tguiWindow.Panel.SetStaticData(new TguiData().Object("labels", labels).ToString());
        PublishTgui();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is JukeboxState queue)
        {
            _queue = queue;
            if (_catalogTab == "queue") BrowseCatalog();
            PublishTgui();
        }
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is JukeboxMetadataMessage metadata)
        {
            foreach (var (id, length) in metadata.Lengths) _trackLengths[id] = length;
            Reload();
            return;
        }
        if (message is not JukeboxLibraryMessage library) return;
        if (library.Revision >= 0 && JukeboxRules.ValidLibrary(library.Library) && library.Status != "save-error")
        {
            _library = library.Library;
            _libraryLoaded = true;
        }
        _libraryBusy = library.Busy;
        _libraryStatus = library.Status;
        if (_catalogTab is "favorites" or "playlists") BrowseCatalog();
        PublishTgui();
    }

    private void PublishTgui()
    {
        if (_tguiWindow == null || !EntMan.TryGetComponent<JukeboxComponent>(Owner, out var jukebox)) return;
        var selected = jukebox.SelectedSongId?.Id ?? "";
        var position = 0f;
        var playing = false;
        var paused = false;
        if (EntMan.TryGetComponent<AudioComponent>(jukebox.AudioStream, out var audio))
        {
            playing = audio.State == AudioState.Playing;
            paused = audio.State == AudioState.Paused;
            if (playing || paused)
                position = (float) ((audio.PauseTime ?? IoCManager.Resolve<IGameTiming>().CurTime) - audio.AudioStart).TotalSeconds;
        }
        var data = new TguiData()
            .Array("tracks", _page.Rows.Select(e => e.Track).Append(selected).Where(id => id.Length > 0).Distinct().Select(TrackData))
            .Array("rows", _page.Rows.Select(e => new TguiData().String("id", e.Id).String("track", e.Track).Number("sourceIndex", e.SourceIndex)))
            .Number("total", _page.Total).Number("sourceCount", _page.SourceCount).Number("page", _page.Index)
            .Number("catalogCount", _catalog?.Count ?? 0)
            .Bool("catalogBusy", _browse != null).String("catalogTab", _pageTab)
            .String("selected", selected).Number("position", Math.Clamp(position, 0, _trackLengths.GetValueOrDefault(selected)))
            .Bool("playing", playing).Bool("paused", paused).Bool("powered", _queue.Powered).Number("gain", jukebox.Gain)
            .Number("repeat", (int) _queue.Repeat).Number("revision", _queue.Revision)
            .Array("queue", _queue.Queue.Select(t => new TguiData().String("id", t.Id).String("track", t.Track)))
            .Bool("libraryLoaded", _libraryLoaded).Bool("libraryBusy", _libraryBusy).String("status", _libraryStatus)
            .Array("favorites", _library.Favorites.Select(id => new TguiData().String("id", id)))
            .Array("playlists", _library.Playlists.Select(p => new TguiData().String("name", p.Key)
                .Array("tracks", p.Value.Select(id => new TguiData().String("id", id)))));
        _tguiWindow.Panel.SetIncrementalState("Jukebox", data, Loc.GetString("dl-jukebox-title"));
    }

    private void OnTguiAction(string action, string payload)
    {
        if (action == "close") { Close(); return; }
        if (action == "focus") { _tguiWindow?.Panel.Web.FocusTextInput(); return; }
        if (action == "classic") { DeferClassic(); return; }
        if (!TguiActionData.TryParse(payload, out var data, JukeboxRules.MaxJson + 8192) || data == null) return;
        if (action == "browse")
        {
            var tab = data.String("tab") ?? "catalog";
            if (tab is not ("catalog" or "favorites" or "queue" or "playlists")) return;
            _catalogTab = tab;
            var query = data.String("query") ?? "";
            _catalogQuery = query[..Math.Min(256, query.Length)];
            _catalogPlaylist = data.String("playlist") ?? "";
            data.TryInt("page", out _catalogPage);
            _catalogPage = Math.Max(0, _catalogPage);
            BrowseCatalog();
            return;
        }
        if (action == "random")
        {
            if (_catalog?.RandomTrack() is { } random) SendMessage(new JukeboxQueueMessage(JukeboxAction.Add, random));
            return;
        }
        var id = data.String("id") ?? "";
        data.TryInt("index", out var index);
        if (action == "play") { SendMessage(new JukeboxPlayingMessage()); return; }
        if (action == "pause") { SendMessage(new JukeboxPauseMessage()); return; }
        if (action == "stop") { SendMessage(new JukeboxStopMessage()); return; }
        if (action == "seek" && data.TryFloat("value", out var time)) { SetTime(time); return; }
        if (action == "gain" && data.TryFloat("value", out var gain)) { SetGain(gain); return; }
        var queueAction = action switch
        {
            "add" => JukeboxAction.Add, "add-first" => JukeboxAction.AddFirst, "move" => JukeboxAction.Move,
            "remove" => JukeboxAction.Remove, "clear" => JukeboxAction.Clear, "next" => JukeboxAction.Next,
            "repeat" => JukeboxAction.Repeat, "play-track" => JukeboxAction.PlayTrack, _ => (JukeboxAction?) null,
        };
        if (queueAction != null)
        {
            data.TryInt("revision", out var revision);
            SendMessage(new JukeboxQueueMessage(queueAction.Value, id, index, revision)); return;
        }
        var libraryAction = action switch
        {
            "library-load" => JukeboxLibraryAction.Load, "favorite" => JukeboxLibraryAction.Favorite,
            "playlist-create" => JukeboxLibraryAction.Create, "playlist-rename" => JukeboxLibraryAction.Rename,
            "playlist-delete" => JukeboxLibraryAction.Delete, "playlist-add" => JukeboxLibraryAction.AddTrack,
            "playlist-remove" => JukeboxLibraryAction.RemoveTrack, "playlist-move" => JukeboxLibraryAction.MoveTrack,
            "favorite-move" => JukeboxLibraryAction.MoveFavorite, "playlist-queue" => JukeboxLibraryAction.QueuePlaylist,
            "library-import" => JukeboxLibraryAction.Import, _ => (JukeboxLibraryAction?) null,
        };
        if (libraryAction == null || _libraryBusy || !_libraryLoaded && libraryAction != JukeboxLibraryAction.Load) return;
        _libraryBusy = true; _libraryStatus = ""; PublishTgui();
        SendMessage(new JukeboxLibraryRequest(libraryAction.Value, id, data.String("playlist") ?? "", data.String("value") ?? "", index));
    }

    private void SwitchInterface(bool tgui)
    {
        if (!IsOpened) return;
        CloseTgui();
        if (_menu is { } native)
            WindowInterfaceStyle.HideForSwitch(this, native);
        _classic = !tgui;
        _readyWait = 0;
        Open();
    }
}

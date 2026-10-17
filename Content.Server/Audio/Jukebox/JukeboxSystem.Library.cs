using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Content.Server.Database;
using Content.Shared.Audio.Jukebox;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server.Audio.Jukebox;

public sealed partial class JukeboxSystem
{
    [Dependency] private IServerDbManager _db = default!;
    private readonly HashSet<NetUserId> _libraryBusy = new();
    private static readonly JsonSerializerOptions LibraryJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };

    private void OnLibrary(Entity<JukeboxComponent> ent, ref JukeboxLibraryRequest args) => ChangeLibraryAsync(ent, args);

    private async void ChangeLibraryAsync(Entity<JukeboxComponent> ent, JukeboxLibraryRequest args)
    {
        // Copy ref-event fields before the first await. Identity always comes from the BUI actor.
        var request = args;
        var actor = args.Actor;
        if (!TryComp<ActorComponent>(actor, out var actorComp) || request.Track == null || request.Playlist == null || request.Value == null ||
            request.Track.Length > 128 || request.Playlist.Length > 35 || request.Value.Length > JukeboxRules.MaxJson) return;
        var session = actorComp.PlayerSession;
        if (!_libraryBusy.Add(session.UserId))
        {
            // This request was not accepted. Do not leave a newly opened window waiting
            // for a response that may be addressed to an already closed window.
            _ui.ServerSendUiMessage(ent.Owner, JukeboxUiKey.Key, new JukeboxLibraryMessage(new(), -1, "busy"), actor);
            return;
        }
        var library = new JukeboxLibrary();
        var status = "";
        try
        {
            var json = await _db.GetJukeboxLibraryAsync(session.UserId);
            library = ReadLibrary(json) ?? throw new JsonException("Invalid persisted jukebox library");
            if (TerminatingOrDeleted(ent.Owner) || !TryComp<ActorComponent>(actor, out var current) || current.PlayerSession != session ||
                !_ui.IsUiOpen(ent.Owner, JukeboxUiKey.Key, actor)) return;
            if (request.Action == JukeboxLibraryAction.Load) { SendLibrary(ent, actor, library); return; }
            if (request.Action == JukeboxLibraryAction.QueuePlaylist)
            {
                if (library.Playlists.TryGetValue(request.Playlist, out var tracks) && AddTracks(ent, tracks))
                { Publish(ent); status = "queued"; }
                else status = "queue-full";
                SendLibrary(ent, actor, library, status);
                return;
            }
            var changed = library.Copy();
            if (!ChangeLibrary(changed, request) || !JukeboxRules.ValidLibrary(changed))
            { SendLibrary(ent, actor, library, "invalid"); return; }
            var serialized = JsonSerializer.Serialize(changed);
            if (serialized.Length > JukeboxRules.MaxJson || !await _db.SaveJukeboxLibraryAsync(session.UserId, serialized))
            { SendLibrary(ent, actor, library, "save-error"); return; }
            library = changed;
            status = "saved";
            // Keep multiple jukebox windows of this actor consistent, without broadcasting private lists.
            var query = EntityQueryEnumerator<JukeboxComponent, UserInterfaceComponent>();
            while (query.MoveNext(out var uid, out _, out _))
                _ui.ServerSendUiMessage(uid, JukeboxUiKey.Key, new JukeboxLibraryMessage(library, 0, status), actor);
        }
        catch (Exception exception)
        {
            Log.Error($"Jukebox library operation failed for {session.UserId}: {exception}");
            SendLibrary(ent, actor, library, "save-error");
        }
        finally { _libraryBusy.Remove(session.UserId); }
    }

    private void SendLibrary(Entity<JukeboxComponent> ent, EntityUid actor, JukeboxLibrary library, string status = "") =>
        _ui.ServerSendUiMessage(ent.Owner, JukeboxUiKey.Key, new JukeboxLibraryMessage(library, 0, status), actor);

    public static JukeboxLibrary? ReadLibrary(string json)
    {
        if (json.Length > JukeboxRules.MaxJson) return null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (!UniqueKeys(doc.RootElement)) return null;
            var library = JsonSerializer.Deserialize<JukeboxLibrary>(json, LibraryJson);
            return JukeboxRules.ValidLibrary(library) ? library : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool UniqueKeys(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
                if (!names.Add(property.Name) || !UniqueKeys(property.Value)) return false;
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) if (!UniqueKeys(item)) return false;
        return true;
    }

    private bool ChangeLibrary(JukeboxLibrary library, JukeboxLibraryRequest request)
    {
        library.Playlists.TryGetValue(request.Playlist, out var tracks);
        switch (request.Action)
        {
            case JukeboxLibraryAction.Favorite:
                if (!JukeboxRules.ValidTrack(request.Track)) return false;
                if (!library.Favorites.Remove(request.Track))
                {
                    if (!_protoManager.HasIndex<JukeboxPrototype>(request.Track)) return false;
                    library.Favorites.Add(request.Track);
                }
                return true;
            case JukeboxLibraryAction.Create:
                return JukeboxRules.ValidName(request.Value) && library.Playlists.TryAdd(request.Value, new());
            case JukeboxLibraryAction.Rename:
                if (tracks == null || !JukeboxRules.ValidName(request.Value) || library.Playlists.ContainsKey(request.Value)) return false;
                library.Playlists.Remove(request.Playlist); library.Playlists[request.Value] = tracks; return true;
            case JukeboxLibraryAction.Delete: return library.Playlists.Remove(request.Playlist);
            case JukeboxLibraryAction.AddTrack:
                if (tracks == null || !_protoManager.HasIndex<JukeboxPrototype>(request.Track) || tracks.Contains(request.Track)) return false;
                tracks.Add(request.Track); return true;
            case JukeboxLibraryAction.RemoveTrack: return tracks?.Remove(request.Track) ?? false;
            case JukeboxLibraryAction.MoveTrack: return tracks != null && JukeboxRules.Move(tracks, tracks.IndexOf(request.Track), request.Index);
            case JukeboxLibraryAction.MoveFavorite: return JukeboxRules.Move(library.Favorites, library.Favorites.IndexOf(request.Track), request.Index);
            case JukeboxLibraryAction.Import:
                var imported = ReadLibrary(request.Value);
                if (imported == null) return false;
                // Merge explicitly: importing never silently replaces an existing named playlist.
                if (imported.Playlists.Keys.Any(library.Playlists.ContainsKey)) return false;
                library.Favorites = library.Favorites.Concat(imported.Favorites).Distinct().ToList();
                foreach (var (name, list) in imported.Playlists) library.Playlists.Add(name, list);
                return true;
            default: return false;
        }
    }
}

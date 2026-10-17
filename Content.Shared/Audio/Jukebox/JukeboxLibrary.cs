using System.Linq;
using Robust.Shared.Serialization;

namespace Content.Shared.Audio.Jukebox;

[Serializable, NetSerializable]
public sealed class JukeboxLibrary
{
    public List<string> Favorites { get; set; } = new();
    public Dictionary<string, List<string>> Playlists { get; set; } = new();
    public JukeboxLibrary Copy() => new() { Favorites = new(Favorites), Playlists = Playlists.ToDictionary(p => p.Key, p => new List<string>(p.Value)) };
}

public static class JukeboxRules
{
    public const int MaxQueue = 256, MaxFavorites = 1024, MaxPlaylists = 40, MaxPlaylistTracks = 256, MaxJson = 262144;
    public static bool ValidTrack(string? id) => id is { Length: > 0 and <= 128 } && !id.Any(char.IsControl);
    public static bool ValidName(string? name) => name is { Length: > 0 and <= 35 } && name.Trim() == name && !name.Any(char.IsControl);
    public static bool ValidLibrary(JukeboxLibrary? library) => library != null &&
        library.Favorites != null && library.Playlists != null && library.Favorites.Count <= MaxFavorites &&
        library.Playlists.Count <= MaxPlaylists && ValidTracks(library.Favorites) &&
        library.Playlists.All(p => ValidName(p.Key) && p.Value != null && p.Value.Count <= MaxPlaylistTracks && ValidTracks(p.Value));
    private static bool ValidTracks(List<string> tracks) => tracks.All(ValidTrack) && tracks.Distinct().Count() == tracks.Count;
    public static bool Move<T>(List<T> list, int from, int to)
    {
        if (from < 0 || from >= list.Count || to < 0 || to >= list.Count || from == to) return false;
        var item = list[from]; list.RemoveAt(from); list.Insert(to, item); return true;
    }
    /// <summary>Track repeat applies to natural completion only; Next always advances.</summary>
    public static string? Advance(List<JukeboxQueueEntry> queue, string? current, JukeboxRepeat repeat, bool skipped)
    {
        if (!skipped && repeat == JukeboxRepeat.Track) return current;
        if (queue.Count == 0) return repeat == JukeboxRepeat.Queue ? current : null;
        var next = queue[0].Track; queue.RemoveAt(0);
        if (repeat == JukeboxRepeat.Queue && current != null)
            queue.Add(new JukeboxQueueEntry(Guid.NewGuid().ToString("N"), current));
        return next;
    }
}
[Serializable, NetSerializable]
public sealed record JukeboxQueueEntry(string Id, string Track);
[Serializable, NetSerializable]
public enum JukeboxRepeat : byte { Off, Track, Queue }
[Serializable, NetSerializable]
public enum JukeboxAction : byte { Add, AddFirst, Move, Remove, Clear, Next, Repeat, PlayTrack }
[Serializable, NetSerializable]
public enum JukeboxLibraryAction : byte { Load, Favorite, Create, Rename, Delete, AddTrack, RemoveTrack, MoveTrack, MoveFavorite, QueuePlaylist, Import }
[Serializable, NetSerializable]
public sealed class JukeboxQueueMessage(JukeboxAction action, string id = "", int index = 0, int revision = 0) : BoundUserInterfaceMessage
{
    public JukeboxAction Action = action;
    public string Id = id;
    public int Index = index;
    public int Revision = revision;
}
[Serializable, NetSerializable]
public sealed class JukeboxLibraryRequest(JukeboxLibraryAction action, string track = "", string playlist = "", string value = "", int index = 0, int revision = 0) : BoundUserInterfaceMessage
{
    public JukeboxLibraryAction Action = action;
    public string Track = track;
    public string Playlist = playlist;
    public string Value = value;
    public int Index = index;
    public int Revision = revision;
}
[Serializable, NetSerializable]
public sealed class JukeboxLibraryMessage(JukeboxLibrary library, int revision, string status = "", bool busy = false) : BoundUserInterfaceMessage
{
    public JukeboxLibrary Library = library;
    public int Revision = revision;
    public string Status = status;
    public bool Busy = busy;
}
[Serializable, NetSerializable]
public sealed class JukeboxState(JukeboxQueueEntry[] queue, JukeboxRepeat repeat, int revision, bool powered) : BoundUserInterfaceState
{
    public JukeboxQueueEntry[] Queue = queue;
    public JukeboxRepeat Repeat = repeat;
    public int Revision = revision;
    public bool Powered = powered;
}

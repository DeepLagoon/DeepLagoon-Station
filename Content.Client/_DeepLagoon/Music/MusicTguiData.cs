using Content.Shared._DeepLagoon.Music;
using Content.Client._DeepLagoon.WebUI;

namespace Content.Client._DeepLagoon.Music;

public static class MusicTguiData
{
    private static readonly string[] Keys =
    [
        "title", "search", "searching", "no-results", "now-playing", "nothing-playing", "play", "pause", "resume",
        "stop", "next", "volume", "position", "duration", "global", "close", "loading", "stopped", "playing",
        "paused", "progress", "search-error", "not-configured", "api-error", "rate-limited", "empty-query",
        "invalid-token", "listeners", "unavailable", "artist-unknown", "command-help", "command-desc",
    ];

    private static readonly string[] StatusKeys =
    [
        "search-error", "not-configured", "api-error", "rate-limited", "empty-query", "invalid-token",
    ];

    public static TguiData Labels()
    {
        var labels = new TguiData();
        foreach (var key in Keys)
            labels.String(key, Loc.GetString("dl-music-" + key));

        return labels;
    }

    public static IReadOnlyList<string> AllKeys => Keys;

    public static IReadOnlyList<string> AllStatusKeys => StatusKeys;

    public static string State(MusicPlaybackStatus status) => status switch
    {
        MusicPlaybackStatus.Playing => "playing",
        MusicPlaybackStatus.Paused => "paused",
        MusicPlaybackStatus.Downloading => "loading",
        _ => "stopped",
    };

    public static string Status(MusicSearchStatus status) => status switch
    {
        MusicSearchStatus.Disabled => "not-configured",
        MusicSearchStatus.NoToken => "not-configured",
        MusicSearchStatus.InvalidToken => "invalid-token",
        MusicSearchStatus.BadQuery => "empty-query",
        MusicSearchStatus.RateLimited => "rate-limited",
        MusicSearchStatus.UpstreamError => "search-error",
        _ => string.Empty,
    };

    public static TguiData Build(
        string query,
        IReadOnlyList<MusicTrack> results,
        bool searching,
        MusicSearchStatus searchStatus,
        MusicTrack? current,
        MusicPlaybackStatus playback,
        float position,
        float length,
        float gain,
        bool global,
        int listeners,
        float progress)
    {
        var rows = new List<TguiData>(results.Count);
        foreach (var track in results)
        {
            rows.Add(new TguiData()
                .String("id", track.Id)
                .String("title", track.Title)
                .String("artist", track.Artist)
                .String("album", track.Album)
                .Number("duration", track.Duration));
        }

        var data = new TguiData()
            .String("query", query)
            .Array("results", rows)
            .Bool("searching", searching)
            .String("status", Status(searchStatus))
            .String("state", State(playback))
            .Number("position", position)
            .Number("length", length)
            .Number("gain", gain)
            .Bool("global", global)
            .Number("listeners", listeners)
            .Number("progress", progress);

        if (current != null)
        {
            data.Object("current", new TguiData()
                .String("id", current.Id)
                .String("title", current.Title)
                .String("artist", current.Artist));
        }

        return data;
    }
}

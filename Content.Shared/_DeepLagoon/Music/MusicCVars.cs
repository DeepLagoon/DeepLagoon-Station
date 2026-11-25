using Robust.Shared.Configuration;

namespace Content.Shared._DeepLagoon.Music;

[CVarDefs]
public sealed class MusicCVars
{
    public static readonly CVarDef<bool> Enabled =
        CVarDef.Create("dl_music.enabled", false, CVar.SERVERONLY);

    public static readonly CVarDef<string> Token =
        CVarDef.Create("dl_music.token", string.Empty, CVar.SERVERONLY | CVar.CONFIDENTIAL);

    public static readonly CVarDef<string> ApiBase =
        CVarDef.Create("dl_music.api_base", "https://api.music.yandex.net", CVar.SERVERONLY);

    public static readonly CVarDef<int> BitrateKbps =
        CVarDef.Create("dl_music.bitrate_kbps", 192, CVar.SERVERONLY);

    public static readonly CVarDef<int> MaxTrackMb =
        CVarDef.Create("dl_music.max_track_mb", 128, CVar.SERVERONLY);

    public static readonly CVarDef<int> CacheMinutes =
        CVarDef.Create("dl_music.cache_minutes", 30, CVar.SERVERONLY);

    public static readonly CVarDef<int> SearchLimit =
        CVarDef.Create("dl_music.search_limit", 100, CVar.SERVERONLY);

    public static readonly CVarDef<int> QueueLimit =
        CVarDef.Create("dl_music.queue_limit", 25, CVar.SERVERONLY);

    public static readonly CVarDef<float> GlobalGain =
        CVarDef.Create("dl_music.global_gain", 0.75f, CVar.SERVERONLY);

    public static readonly CVarDef<float> HttpTimeoutSeconds =
        CVarDef.Create("dl_music.http_timeout_seconds", 15f, CVar.SERVERONLY);
}

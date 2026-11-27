using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Content.Shared._DeepLagoon.Music;
using Robust.Shared.Configuration;
using Robust.Shared.IoC;
using Robust.Shared.Log;

namespace Content.Server._DeepLagoon.Music;

public sealed class MusicApi : IDisposable, IPostInjectInit
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly ILogManager _log = default!;

    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private ISawmill _sawmill = default!;

    void IPostInjectInit.PostInject() => Initialize();

    public void Initialize()
    {
        _sawmill = _log.GetSawmill("dl.music");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ShitcodeStation-Music/1.0");
    }

    public bool Configured => _cfg.GetCVar(MusicCVars.Enabled) && Token.Length > 0;

    private string Token => MusicRules.SanitizeToken(_cfg.GetCVar(MusicCVars.Token));

    private string ApiBase => _cfg.GetCVar(MusicCVars.ApiBase).TrimEnd('/');

    private TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(_cfg.GetCVar(MusicCVars.HttpTimeoutSeconds), 2f, 60f));

    public async Task<(List<MusicTrack> Tracks, MusicSearchStatus Status)> SearchAsync(string query, CancellationToken cancel)
    {
        if (!_cfg.GetCVar(MusicCVars.Enabled))
            return ([], MusicSearchStatus.Disabled);

        if (string.IsNullOrWhiteSpace(Token))
            return ([], MusicSearchStatus.NoToken);

        if (!MusicRules.ValidQuery(query, out var cleaned))
            return ([], MusicSearchStatus.BadQuery);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(Timeout);

            var wanted = Math.Clamp(_cfg.GetCVar(MusicCVars.SearchLimit), 1, 200);
            var found = new List<MusicTrack>(wanted);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (var page = 0; page < 5 && found.Count < wanted; page++)
            {
                var url = $"{ApiBase}/search?type=track&page={page}&text={Uri.EscapeDataString(cleaned)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("OAuth", Token);

                using var resp = await _http.SendAsync(req, cts.Token);
                if (resp.StatusCode is HttpStatusCode.TooManyRequests)
                    return (found, found.Count > 0 ? MusicSearchStatus.Ok : MusicSearchStatus.RateLimited);

                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    _sawmill.Warning("Music token rejected by the API: {Status}", (int) resp.StatusCode);
                    return ([], MusicSearchStatus.InvalidToken);
                }

                if (!resp.IsSuccessStatusCode)
                {
                    _sawmill.Warning("Music search failed: {Status}", (int) resp.StatusCode);
                    return (found, found.Count > 0 ? MusicSearchStatus.Ok : MusicSearchStatus.UpstreamError);
                }

                var text = await resp.Content.ReadAsStringAsync(cts.Token);
                var pageTracks = ExtractTracks(text, wanted);

                if (pageTracks.Count == 0)
                    break;

                foreach (var track in pageTracks)
                {
                    if (!seen.Add(track.Id))
                        continue;

                    found.Add(track);

                    if (found.Count >= wanted)
                        break;
                }
            }

            return (found, MusicSearchStatus.Ok);
        }
        catch (OperationCanceledException)
        {
            return ([], MusicSearchStatus.UpstreamError);
        }
        catch (Exception e)
        {
            _sawmill.Warning("Music search error: {Error}", e.GetType().Name);
            return ([], MusicSearchStatus.UpstreamError);
        }
    }

    private static List<MusicTrack> ExtractTracks(string json, int limit)
    {
        var list = new List<MusicTrack>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return list;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Object)
                return list;

            if (!result.TryGetProperty("tracks", out var tracks) ||
                tracks.ValueKind != JsonValueKind.Object)
                return list;

            if (!tracks.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in results.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                if (!item.TryGetProperty("id", out var idProp))
                    continue;

                var id = idProp.ValueKind switch
                {
                    JsonValueKind.String => idProp.GetString(),
                    JsonValueKind.Number => idProp.GetRawText(),
                    _ => null,
                };

                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var title = item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                    ? MusicRules.Clean(t.GetString(), MusicConstants.MaxTitleLength)
                    : string.Empty;

                if (title.Length == 0)
                    continue;

                var artist = string.Empty;
                if (item.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
                {
                    var names = new List<string>();
                    foreach (var a in artists.EnumerateArray())
                    {
                        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("name", out var n) ||
                            n.ValueKind != JsonValueKind.String)
                            continue;

                        var name = MusicRules.Clean(n.GetString(), MusicConstants.MaxArtistLength);
                        if (name.Length > 0)
                            names.Add(name);
                    }

                    artist = MusicRules.Clean(string.Join(", ", names), MusicConstants.MaxArtistLength);
                }

                var duration = 0f;
                if (item.TryGetProperty("durationMs", out var ms) && ms.ValueKind == JsonValueKind.Number &&
                    ms.TryGetInt64(out var durationMs) && durationMs > 0)
                    duration = durationMs / 1000f;

                var album = string.Empty;
                if (item.TryGetProperty("albums", out var albums) && albums.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in albums.EnumerateArray())
                    {
                        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("title", out var at) ||
                            at.ValueKind != JsonValueKind.String)
                            continue;

                        album = MusicRules.Clean(at.GetString(), MusicConstants.MaxAlbumLength);
                        break;
                    }
                }

                list.Add(new MusicTrack(id!, title, artist, duration, album));
                if (list.Count >= limit)
                    break;
            }
        }

        return list;
    }

    public async Task<DecodedTrack?> FetchAsync(string trackId, CancellationToken cancel)
    {
        if (!MusicRules.ValidTrackId(trackId, out var id))
            return null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(Timeout);

            var url = await ResolveDownloadUrl(id, cts.Token);
            if (url == null)
                return null;

            var maxPcm = (long) Math.Clamp(_cfg.GetCVar(MusicCVars.MaxTrackMb), 1, 2048) * 1024 * 1024;
            var maxBytes = Math.Max(4L * 1024 * 1024, maxPcm / 4);

            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _sawmill.Warning("Music download failed: {Status}", (int) resp.StatusCode);
                return null;
            }

            if (resp.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            {
                _sawmill.Warning("Music track rejected: {Bytes} bytes exceeds cap", declared);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cts.Token)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    _sawmill.Warning("Music track exceeded the size cap while downloading");
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            var bytes = buffer.ToArray();

            var maxPcmLocal = maxPcm;
            return await Task.Run(() => MusicTranscoder.ToWav(bytes, _sawmill, maxPcmLocal), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception e)
        {
            _sawmill.Warning("Music fetch error: {Error}", e.GetType().Name);
            return null;
        }
    }

    private async Task<string?> ResolveDownloadUrl(string id, CancellationToken cancel)
    {
        var bitrate = Math.Clamp(_cfg.GetCVar(MusicCVars.BitrateKbps), 64, 320);
        var url = $"{ApiBase}/tracks/{Uri.EscapeDataString(id)}/download-info";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("OAuth", Token);

        using var resp = await _http.SendAsync(req, cancel);
        if (!resp.IsSuccessStatusCode)
        {
            _sawmill.Warning("Music download-info failed: {Status}", (int) resp.StatusCode);
            return null;
        }

        var text = await resp.Content.ReadAsStringAsync(cancel);
        var infos = ExtractDownloadInfos(text);
        if (infos.Count == 0)
            return null;

        DownloadInfo? best = null;
        foreach (var info in infos)
        {
            if (info.DownloadInfoUrl == null)
                continue;

            var isMp3 = string.Equals(info.Codec, "mp3", StringComparison.OrdinalIgnoreCase);
            if (best == null)
            {
                best = info;
                continue;
            }

            var bestIsMp3 = string.Equals(best.Codec, "mp3", StringComparison.OrdinalIgnoreCase);
            if (isMp3 && !bestIsMp3)
                best = info;
            else if (isMp3 == bestIsMp3 && Math.Abs(info.BitrateInKbps - bitrate) < Math.Abs(best.BitrateInKbps - bitrate))
                best = info;
        }

        if (best == null)
            return null;

        return await ResolveStorageUrl(best.DownloadInfoUrl!, cancel);
    }

    private async Task<string?> ResolveStorageUrl(string infoUrl, CancellationToken cancel)
    {
        using var resp = await _http.GetAsync(infoUrl, cancel);
        if (!resp.IsSuccessStatusCode)
            return null;

        var xml = await resp.Content.ReadAsStringAsync(cancel);
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml);

        var host = doc.DocumentElement?.SelectSingleNode("host")?.InnerText;
        var path = doc.DocumentElement?.SelectSingleNode("path")?.InnerText;
        var ts = doc.DocumentElement?.SelectSingleNode("ts")?.InnerText;
        var s = doc.DocumentElement?.SelectSingleNode("s")?.InnerText;

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(path) ||
            string.IsNullOrWhiteSpace(ts) || string.IsNullOrWhiteSpace(s))
            return null;

        var signSource = $"XGRlBW9FXlekgbPrRHuSiA{path!.Substring(1)}{s}";
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(signSource));
        var sign = Convert.ToHexString(hash).ToLowerInvariant();

        return $"https://{host}/get-mp3/{sign}/{ts}{path}";
    }

    private static List<DownloadInfo> ExtractDownloadInfos(string json)
    {
        var list = new List<DownloadInfo>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return list;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var entry in result.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                if (!entry.TryGetProperty("downloadInfoUrl", out var urlProp) ||
                    urlProp.ValueKind != JsonValueKind.String)
                    continue;

                var codec = entry.TryGetProperty("codec", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null;

                var bitrate = entry.TryGetProperty("bitrateInKbps", out var b) && b.ValueKind == JsonValueKind.Number &&
                              b.TryGetInt32(out var kbps)
                    ? kbps
                    : 0;

                list.Add(new DownloadInfo
                {
                    Codec = codec,
                    BitrateInKbps = bitrate,
                    DownloadInfoUrl = urlProp.GetString(),
                });
            }
        }

        return list;
    }

    public void Dispose() => _http.Dispose();

    private sealed class DownloadInfo
    {
        [JsonPropertyName("codec")]
        public string? Codec { get; set; }

        [JsonPropertyName("bitrateInKbps")]
        public int BitrateInKbps { get; set; }

        [JsonPropertyName("downloadInfoUrl")]
        public string? DownloadInfoUrl { get; set; }
    }
}

public sealed class DecodedTrack
{
    public byte[] Pcm { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public float Duration { get; }

    public DecodedTrack(byte[] pcm, int sampleRate, int channels)
    {
        Pcm = pcm;
        SampleRate = sampleRate;
        Channels = channels;
        Duration = sampleRate > 0 && channels > 0 ? pcm.Length / (float) (sampleRate * channels * 2) : 0f;
    }
}

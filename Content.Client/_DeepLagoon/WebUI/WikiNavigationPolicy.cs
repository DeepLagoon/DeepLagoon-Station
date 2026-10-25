using System.Linq;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>Wiki access belongs exclusively to WikiGuidebookWindow, never to GameWebView.</summary>
public static class WikiNavigationPolicy
{
    public const string Origin = "https://wiki.deep-lagoon-ss14.ru";

    public static bool IsPage(string url, IReadOnlySet<string> paths, string origin = Origin, string treeRoot = "/ru/in_game")
    {
        return TryUri(url, out var uri) && IsWiki(uri, origin) &&
            uri.Query.Length == 0 && IsPath(uri.AbsolutePath) && (paths.Contains(uri.AbsolutePath) || (IsPath(treeRoot) && (uri.AbsolutePath == treeRoot || uri.AbsolutePath.StartsWith(treeRoot + "/", StringComparison.Ordinal))));
    }

    public static bool IsTreeRequest(string url, string origin = Origin) => TryUri(url, out var uri) && IsWiki(uri, origin) && uri.AbsolutePath == "/graphql" && uri.Query.Length == 0;

    public static bool IsAsset(string url, string origin = Origin)
    {
        return TryUri(url, out var uri) && IsWiki(uri, origin) &&
            (uri.AbsolutePath.StartsWith("/_assets/", StringComparison.Ordinal) ||
             uri.AbsolutePath.StartsWith("/wiki-design-assets/", StringComparison.Ordinal) ||
             uri.AbsolutePath.StartsWith("/guidebook/in_game/assets/", StringComparison.Ordinal) ||
             uri.AbsolutePath == "/anima_00168_.png" || uri.AbsolutePath == "/favicon.ico");
    }

    public static bool IsPath(string path) => path.Length > 1 && path.StartsWith("/", StringComparison.Ordinal) &&
        !path.Any(char.IsControl) && !path.Any(char.IsWhiteSpace) && !path.Contains('?') && !path.Contains('#') && !path.Contains('%') &&
        !path.Contains('\\') && !path.Contains("..") && !path.Contains("//");

    public static bool IsOrigin(string origin) => TryUri(origin, out var uri) &&
        uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static bool IsWiki(Uri uri, string origin) => IsOrigin(origin) &&
        TryUri(origin, out var configured) && uri.Scheme == configured.Scheme &&
        uri.Host == configured.Host && uri.IsDefaultPort && uri.UserInfo.Length == 0;

    // UriKind is not exposed by Robust's sandbox. The Uri(string) constructor is.
    public static bool TryUri(string url, out Uri uri)
    {
        // An unset TGUI development-server address is valid configuration, not a URI.
        if (string.IsNullOrWhiteSpace(url)) { uri = default!; return false; }
        try { uri = new Uri(url); return uri.IsAbsoluteUri; }
        catch (Exception) { uri = default!; return false; }
    }
}

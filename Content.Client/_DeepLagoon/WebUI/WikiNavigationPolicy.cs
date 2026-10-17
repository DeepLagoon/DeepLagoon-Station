namespace Content.Client._DeepLagoon.WebUI;

/// <summary>Wiki access belongs exclusively to WikiGuidebookWindow, never to GameWebView.</summary>
public static class WikiNavigationPolicy
{
    public const string Origin = "https://wiki.deep-lagoon-ss14.ru";

    public static bool IsPage(string url, IReadOnlySet<string> paths)
    {
        return TryUri(url, out var uri) && IsWiki(uri) &&
            uri.Query.Length == 0 && IsPath(uri.AbsolutePath) && (paths.Contains(uri.AbsolutePath) || uri.AbsolutePath == "/ru/in_game" || uri.AbsolutePath.StartsWith("/ru/in_game/", StringComparison.Ordinal));
    }

    public static bool IsTreeRequest(string url) => TryUri(url, out var uri) && IsWiki(uri) && uri.AbsolutePath == "/graphql" && uri.Query.Length == 0;

    public static bool IsAsset(string url)
    {
        return TryUri(url, out var uri) && IsWiki(uri) &&
            (uri.AbsolutePath.StartsWith("/_assets/", StringComparison.Ordinal) ||
             uri.AbsolutePath == "/anima_00168_.png" || uri.AbsolutePath == "/favicon.ico");
    }

    public static bool IsPath(string path) => path.StartsWith("/ru/", StringComparison.Ordinal) &&
        !path.Contains('?') && !path.Contains('#') && !path.Contains('%') &&
        !path.Contains('\\') && !path.Contains("..") && !path.Contains("//");

    private static bool IsWiki(Uri uri) => uri.Scheme == "https" &&
        uri.Host == "wiki.deep-lagoon-ss14.ru" && uri.IsDefaultPort && uri.UserInfo.Length == 0;

    // UriKind is not exposed by Robust's sandbox. The Uri(string) constructor is.
    public static bool TryUri(string url, out Uri uri)
    {
        // An unset TGUI development-server address is valid configuration, not a URI.
        if (string.IsNullOrWhiteSpace(url)) { uri = default!; return false; }
        try { uri = new Uri(url); return uri.IsAbsoluteUri; }
        catch (Exception) { uri = default!; return false; }
    }
}

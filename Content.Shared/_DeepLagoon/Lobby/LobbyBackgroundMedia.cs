namespace Content.Shared._DeepLagoon.Lobby;

/// <summary>Packaged lobby media only; never a remote URL or an arbitrary web document.</summary>
public static class LobbyBackgroundMedia
{
    public static bool IsSupported(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 512 ||
            !(path.StartsWith("/Textures/", StringComparison.Ordinal) ||
              path.StartsWith("/Web/DeepLagoon/LobbyMedia/", StringComparison.Ordinal)))
            return false;
        foreach (var c in path)
        {
            if (char.IsControl(c) || c is '\\' or '%' or '?' or '#')
                return false;
        }
        foreach (var segment in path.Split('/'))
        {
            if (segment is "." or "..")
                return false;
        }
        return IsAnimated(path) || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAnimated(string path) => path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
}

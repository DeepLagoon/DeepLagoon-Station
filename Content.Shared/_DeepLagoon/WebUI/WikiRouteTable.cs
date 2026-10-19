namespace Content.Shared._DeepLagoon.WebUI;

/// <summary>Server-configured routes. Empty destinations explicitly disable an entry point.</summary>
public sealed class WikiRouteTable
{
    private readonly Dictionary<string, string> _routes;
    public IReadOnlyDictionary<string, string> Routes => _routes;

    public WikiRouteTable(Dictionary<string, string> defaults, string configuration)
    {
        _routes = new(defaults, StringComparer.Ordinal);
        foreach (var raw in configuration.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0)
                throw new ArgumentException("Wiki route must have the form source = destination: " + line);
            var key = line[..separator].Trim();
            if (key.Contains('\t') || key.Contains('\r'))
                throw new ArgumentException("Wiki route source contains whitespace: " + key);
            _routes[key] = line[(separator + 1)..].Trim();
        }
    }

    public bool Contains(string source) => _routes.ContainsKey(source);

    public string Resolve(IEnumerable<string> sources, string fallback = "default")
    {
        foreach (var source in sources)
        {
            if (_routes.ContainsKey(source)) return Follow(source);
        }
        return Follow(fallback);
    }

    private string Follow(string destination)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (_routes.TryGetValue(destination, out var next))
        {
            if (!visited.Add(destination) || visited.Count > 32) return "";
            destination = next;
        }
        return destination;
    }
}

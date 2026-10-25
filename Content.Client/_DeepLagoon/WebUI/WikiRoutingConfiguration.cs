using System.Linq;
using Content.Shared._DeepLagoon.WebUI;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.WebUI;

public sealed partial class WikiRoutingConfiguration
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private string? _previousConfiguration;
    private string? _previousDefaults;
    private WikiRouteTable? _cached;

    public WikiRouteTable Read()
    {
        var defaults = new Dictionary<string, string>
        {
            ["default"] = "page:InGame",
            ["button:guidebook"] = "default",
            ["button:rules"] = "page:Rules",
            ["button:wiki"] = External(_cfg.GetCVar(CCVars.InfoLinksWiki)),
            ["rules:mandatory"] = "local:" + _cfg.GetCVar(CCVars.RulesFile),
        };
        foreach (var button in new[] { "top-guidebook", "key-guidebook", "banner-guidebook", "escape-guidebook", "new-player" })
            defaults["button:" + button] = "button:guidebook";
        foreach (var button in new[] { "banner-rules", "escape-rules", "character-rules" })
            defaults["button:" + button] = "button:rules";
        foreach (var button in new[] { "banner-wiki", "escape-wiki" })
            defaults["button:" + button] = "button:wiki";
        foreach (var page in _prototypes.EnumeratePrototypes<WikiPagePrototype>())
        {
            defaults["page:" + page.ID] = page.Path;
            if (page.Default) defaults["default"] = "page:" + page.ID;
            foreach (var guide in page.GuideEntries) defaults["guide:" + guide] = "page:" + page.ID;
            foreach (var (entity, path) in page.EntityRoutes) defaults["entity:" + entity] = path;
        }
        var configuration = _cfg.GetCVar(CCVars.WikiRoutes);
        var signature = string.Join("\n", defaults.Select(pair => pair.Key + "=" + pair.Value));
        if (_cached != null && _previousConfiguration == configuration && _previousDefaults == signature) return _cached;
        _previousConfiguration = configuration;
        _previousDefaults = signature;
        try { _cached = new WikiRouteTable(defaults, configuration); }
        catch (ArgumentException error)
        {
            Logger.GetSawmill(Robust.Shared.Log.LogManager.ROOT).Error("Invalid wiki.routes; using prototype defaults: " + error.Message);
            _cached = new WikiRouteTable(defaults, "");
        }
        return _cached;
    }

    public string Origin => _cfg.GetCVar(CCVars.WikiOrigin).TrimEnd('/');
    public string TreeRoot => _cfg.GetCVar(CCVars.WikiTreeRoot).TrimEnd('/');
    public string TreeLocale => _cfg.GetCVar(CCVars.WikiTreeLocale);
    public float NewPlayerMinutes => _cfg.GetCVar(CCVars.WikiNewPlayerMinutes);
    public static string External(string? url) => string.IsNullOrWhiteSpace(url) ? "" : "external:" + url;
}

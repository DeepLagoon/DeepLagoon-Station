using Content.Shared.CCVar;
using Content.Shared.Guidebook;
using Content.Shared.UserInterface;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;

namespace Content.Shared._DeepLagoon.WebUI;

/// <summary>When a paper book is configured to open help, its ordinary BUI must not also open on the server.</summary>
public sealed partial class WikiActivationSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private string? _previous;
    private WikiRouteTable? _routes;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MetaDataComponent, ActivatableUIOpenAttemptEvent>(OnOpenAttempt);
    }

    private void OnOpenAttempt(EntityUid uid, MetaDataComponent metadata, ActivatableUIOpenAttemptEvent args)
    {
        var configuration = _cfg.GetCVar(CCVars.WikiRoutes);
        // The usual configuration leaves every ordinary BUI alone.
        if (!configuration.Contains("activation:", StringComparison.Ordinal) || metadata.EntityPrototype is not {} prototype) return;
        if (_routes == null || _previous != configuration)
        {
            _previous = configuration;
            var defaults = new Dictionary<string, string>();
            foreach (var page in _prototypes.EnumeratePrototypes<WikiPagePrototype>())
            {
                defaults["page:" + page.ID] = page.Path;
                if (page.Default) defaults["default"] = "page:" + page.ID;
                foreach (var guide in page.GuideEntries) defaults["guide:" + guide] = "page:" + page.ID;
            }
            try { _routes = new WikiRouteTable(defaults, configuration); }
            catch (ArgumentException) { _routes = new WikiRouteTable(defaults, ""); }
        }
        var sources = new List<string> { "activation:" + prototype.ID };
        foreach (var (id, _) in _prototypes.EnumerateAllParents<EntityPrototype>(prototype.ID))
            sources.Add("activation:" + id);
        var destination = _routes.Resolve(sources, "");
        var local = destination.StartsWith("local:", StringComparison.Ordinal) &&
            _prototypes.HasIndex<GuideEntryPrototype>(destination[6..]);
        var web = destination.StartsWith("/", StringComparison.Ordinal) || destination.StartsWith("external:https://", StringComparison.Ordinal);
        if (local || web) args.Cancel();
    }
}

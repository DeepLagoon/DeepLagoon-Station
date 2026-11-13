using System.Numerics;
using Content.Server.Hands.Systems;
using Content.Server.Shuttles.Components;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared.Damage;
using Content.Shared.Examine;
using Content.Shared.Popups;
using Content.Shared.Shuttles.Components;
using Content.Shared.Stacks;
using Content.Shared.Verbs;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

[RegisterComponent]
public sealed partial class SpaceNavigationSitesComponent : Component
{
    public SectorDefinition? Definition;
    public string Campaign = "";
    public List<NavigationSite> Sites = new();
    public HashSet<string> Completed = new();
    public Dictionary<string, EntityUid> LiveGrids = new();
}

[RegisterComponent]
public sealed partial class SpaceNavigationCooldownComponent : Component
{
    public TimeSpan ScanReady;
    public TimeSpan RecoverReady;
}

/// <summary>Server-only prototype: survey, choose data or supplies, and record one durable outcome per signal.</summary>
public sealed class SpaceNavigationSystem : EntitySystem
{
    [Dependency] private readonly SectorSystem _sectors = default!;
    [Dependency] private readonly SpaceSurveySystem _survey = default!;
    [Dependency] private readonly OrbitalDebrisSystem _debris = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly SharedStackSystem _stacks = default!;
    [Dependency] private readonly DamageableSystem _damage = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NavigationSignalComponent, ExaminedEvent>(OnSignalExamine);
    }

    public SpaceNavigationSitesComponent Sites(EntityUid map, SpaceEnvironmentComponent environment)
    {
        var cache = EnsureComp<SpaceNavigationSitesComponent>(map);
        var baseline = _sectors.Baseline(map, environment);
        if (ReferenceEquals(cache.Definition, baseline) && cache.Campaign == environment.Campaign) return cache;
        cache.Definition = baseline;
        cache.Campaign = environment.Campaign;
        cache.Sites = NavigationSiteCatalog.Generate(baseline);
        cache.Completed = string.IsNullOrEmpty(environment.Campaign) ? new() : _sectors.Store.Encounters(environment.Campaign, environment.Definition.Id);
        return cache;
    }

    public string SiteName(NavigationSite site) => Loc.GetString("space-navigation-site-" + site.Kind.ToString().ToLowerInvariant());

    public void AddVerbs(Entity<ShuttleConsoleComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!_sectors.Enabled || !args.CanAccess || !args.CanInteract || Transform(ent).MapUid is not { } map
            || !HasComp<SpaceEnvironmentComponent>(map)) return;
        var user = args.User;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("space-navigation-scan"), Act = () => Scan(ent, user) });
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("space-navigation-data"), Act = () => Recover(ent, user, false) });
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("space-navigation-cargo"), Act = () => Recover(ent, user, true) });
    }

    public void AddHint(Entity<ShuttleConsoleComponent> ent, ref ExaminedEvent args)
    {
        if (!_sectors.Enabled || Transform(ent).MapUid is not { } map || !HasComp<SpaceEnvironmentComponent>(map)) return;
        args.PushMarkup(Loc.GetString("space-navigation-hint"));
    }

    private void OnSignalExamine(Entity<NavigationSignalComponent> ent, ref ExaminedEvent args)
    {
        if (Transform(ent).MapUid is not { } map || !TryComp<SpaceEnvironmentComponent>(map, out var environment)) return;
        args.PushMarkup(Loc.GetString(Sites(map, environment).Completed.Contains(ent.Comp.Site)
            ? "space-navigation-recorded" : "space-navigation-relay-hint"));
    }

    public bool Scan(EntityUid console, EntityUid user)
    {
        if (!_survey.CanOperate(console, user) || Transform(console).GridUid is not { } grid) return false;
        var cooldown = EnsureComp<SpaceNavigationCooldownComponent>(grid);
        if (_timing.CurTime < cooldown.ScanReady) return false;
        cooldown.ScanReady = _timing.CurTime + TimeSpan.FromSeconds(5);
        var site = Nearest(console, 1800f, out _);
        if (site == null) { Say(console, user, "space-navigation-empty"); return true; }
        var range = (int) Vector2.Distance(_transform.GetWorldPosition(console), site.Position);
        var hint = Loc.GetString("space-navigation-clue-" + site.Kind.ToString().ToLowerInvariant());
        if (site.Kind == NavigationSiteKind.DustSample && Transform(console).MapUid is { } map
            && TryComp<SpaceEnvironmentComponent>(map, out var environment))
        {
            var region = environment.Definition.Regions.Find(r => "v1/region/" + r.Id == site.Id);
            if (region != null)
            {
                var a = NavigationSiteCatalog.RegionPoint(region, new Vector2(-0.8f, 0));
                var b = NavigationSiteCatalog.RegionPoint(region, new Vector2(0.8f, 0));
                hint += " " + Loc.GetString("space-navigation-corridor", ("ax", (int) a.X), ("ay", (int) a.Y), ("bx", (int) b.X), ("by", (int) b.Y));
            }
        }
        if (site.Kind == NavigationSiteKind.Capacitor)
            hint += " " + Loc.GetString("space-navigation-window", ("seconds", PlasmaWeather.QuietIn(site.Seed, _timing.CurTime.TotalSeconds)));
        _popup.PopupEntity(Loc.GetString("space-navigation-signal", ("name", SiteName(site)), ("x", (int) site.Position.X),
            ("y", (int) site.Position.Y), ("distance", range), ("risk", (int) (site.Intensity * 100)),
            ("quality", 60 + site.Seed % 36)) + "\n" + hint, console, user);
        return true;
    }

    public bool Recover(EntityUid console, EntityUid user, bool cargo)
    {
        if (!_survey.CanOperate(console, user) || Transform(console).GridUid is not { } grid) return false;
        var site = Nearest(console, 80f, out var map);
        if (site == null) { Say(console, user, "space-navigation-approach"); return false; }
        var cooldown = EnsureComp<SpaceNavigationCooldownComponent>(grid);
        if (_timing.CurTime < cooldown.RecoverReady) { Say(console, user, "space-navigation-busy"); return false; }
        if (TryComp<PhysicsComponent>(grid, out var body) && body.LinearVelocity.Length() > (cargo ? 4f : 2f))
        { Say(console, user, "space-navigation-slow"); return false; }
        if (site.Kind == NavigationSiteKind.Capacitor && !PlasmaWeather.Quiet(site.Seed, _timing.CurTime.TotalSeconds))
        { Say(console, user, "space-navigation-wait"); return false; }
        EntityUid? cable = null;
        if (site.Kind == NavigationSiteKind.Satellite && !cargo)
        {
            foreach (var held in _hands.EnumerateHeld(user))
                if (TryComp<StackComponent>(held, out var stack) && stack.StackTypeId == "Cable" && stack.Count >= 5) { cable = held; break; }
            if (cable == null) { Say(console, user, "space-navigation-cable"); return false; }
        }
        var environment = Comp<SpaceEnvironmentComponent>(map);
        // Commit before creating loot or research. Another console/server connection cannot duplicate the outcome.
        if (string.IsNullOrEmpty(environment.Campaign)
            || !_sectors.Store.TryRecordEncounter(environment.Campaign, environment.Definition.Id, site.Id, cargo ? "cargo" : "data"))
        { Say(console, user, "space-navigation-recorded"); return false; }
        Sites(map, environment).Completed.Add(site.Id);
        cooldown.RecoverReady = _timing.CurTime + TimeSpan.FromSeconds(20);
        if (cable is { } consumed) _stacks.Use(consumed, 5);
        var reward = "";
        if (!cargo)
            reward = _survey.AwardResearch(console, grid, user, 1500 + (int) (site.Intensity * 2000));
        else
        {
            var prototype = site.Kind switch
            {
                NavigationSiteKind.Capacitor => "PowerCellHigh",
                NavigationSiteKind.RadiationCache => "ArtifactFragment1",
                NavigationSiteKind.DustSample => "SpaceQuartz",
                NavigationSiteKind.Distress => "MedkitFilled",
                _ => "SheetSteel10",
            };
            var item = Spawn(prototype, Transform(console).Coordinates);
            _hands.TryPickupAnyHand(user, item);
            reward = Loc.GetString("space-navigation-delivered");
            if (site.Kind == NavigationSiteKind.DustSample)
            {
                var shock = new DamageSpecifier();
                shock.DamageDict["Shock"] = 5;
                _damage.TryChangeDamage(user, shock);
            }
        }
        _popup.PopupEntity(Loc.GetString("space-navigation-complete", ("name", SiteName(site))) + "\n" + reward, console, user);
        return true;
    }

    private NavigationSite? Nearest(EntityUid console, float range, out EntityUid map)
    {
        map = default;
        if (Transform(console).MapUid is not { } found || !TryComp<SpaceEnvironmentComponent>(found, out var environment)) return null;
        map = found;
        var sites = Sites(map, environment);
        var position = _transform.GetWorldPosition(console);
        _debris.RefreshExclusions();
        var distance = range * range;
        NavigationSite? selected = null;
        foreach (var site in sites.Sites)
        {
            if (sites.Completed.Contains(site.Id) || !NavigationSiteCatalog.Available(environment.Definition, site)) continue;
            var squared = Vector2.DistanceSquared(position, site.Position);
            if (squared > distance || !_debris.SiteAllowed(map, site.Position)) continue;
            selected = site;
            distance = squared;
        }
        return selected;
    }

    private void Say(EntityUid console, EntityUid user, string message) => _popup.PopupEntity(Loc.GetString(message), console, user);
}

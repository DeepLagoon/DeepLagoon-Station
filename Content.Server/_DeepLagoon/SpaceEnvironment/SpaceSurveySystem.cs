using System.Numerics;
using Content.Server.Power.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared.Access.Systems;
using Content.Shared.ActionBlocker;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Verbs;
using Robust.Shared.Timing;
using Robust.Shared.Prototypes;
using Robust.Shared.Map.Components;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

/// <summary>A first encounter loop: find a signal, approach slowly, collect a persistent survey, or bypass it.</summary>
public sealed class SpaceSurveySystem : EntitySystem
{
    [Dependency] private readonly SectorSystem _sectors = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;
    [Dependency] private readonly AccessReaderSystem _access = default!;
    [Dependency] private readonly SharedShuttleConsoleLockSystem _locks = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ShuttleConsoleComponent, GetVerbsEvent<AlternativeVerb>>(OnVerbs);
        SubscribeLocalEvent<ShuttleConsoleComponent, ExaminedEvent>(OnExamine);
    }

    private void OnVerbs(Entity<ShuttleConsoleComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !TryEnvironment(ent, out _, out _, out _)) return;
        var user = args.User;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("space-environment-survey"), Act = () => Scan(ent, user) });
    }

    private void OnExamine(Entity<ShuttleConsoleComponent> ent, ref ExaminedEvent args)
    {
        if (!TryEnvironment(ent, out var environment, out _, out _)) return;
        args.PushMarkup(Loc.GetString("space-environment-sector", ("sector", environment.Definition.Id),
            ("profile", Loc.GetString(_prototypes.Index<SectorProfilePrototype>(environment.Definition.Profile).Name)),
            ("surveyed", environment.Surveyed.Count), ("total", environment.Definition.Regions.Count)));
        args.PushMarkup(Loc.GetString("space-environment-survey-hint"));
    }

    public bool Scan(EntityUid console, EntityUid user)
    {
        if (!_sectors.Enabled || !Transform(console).Anchored || !this.IsPowered(console, EntityManager)
            || !_blocker.CanInteract(user, console) || !_interaction.InRangeUnobstructed(user, console)
            || !_access.IsAllowed(user, console)
            || TryComp<ShuttleConsoleLockComponent>(console, out var locked) && _locks.GetEffectiveLockState(console, locked)
            || !TryEnvironment(console, out var environment, out var map, out var grid)) return false;
        var cooldown = EnsureComp<SpaceSurveyCooldownComponent>(grid);
        if (_timing.CurTime < cooldown.ReadyAt)
        {
            _popup.PopupEntity(Loc.GetString("space-environment-survey-cooldown"), console, user);
            return false;
        }
        cooldown.ReadyAt = _timing.CurTime + TimeSpan.FromSeconds(20);
        var position = _transform.GetWorldPosition(console);
        SpaceRegion? nearest = null;
        var distance = 1800f;
        foreach (var region in environment.Definition.Regions)
        {
            if (environment.Surveyed.Contains(region.Id)) continue;
            var candidate = Vector2.Distance(position, new Vector2(region.X, region.Y));
            if (candidate >= distance) continue;
            nearest = region;
            distance = candidate;
        }
        if (nearest == null)
        {
            _popup.PopupEntity(Loc.GetString("space-environment-survey-empty"), console, user);
            return true;
        }
        var kind = Loc.GetString("space-environment-kind-" + nearest.Kind.ToString().ToLowerInvariant());
        if (SpaceRegionGeometry.Local(nearest, position).LengthSquared() > 0.55f * 0.55f)
        {
            _popup.PopupEntity(Loc.GetString("space-environment-survey-signal", ("kind", kind),
                ("x", (int) nearest.X), ("y", (int) nearest.Y), ("distance", (int) distance)), console, user);
            return true;
        }
        if (TryComp<Robust.Shared.Physics.Components.PhysicsComponent>(grid, out var physics)
            && physics.LinearVelocity.Length() > 4f)
        {
            _popup.PopupEntity(Loc.GetString("space-environment-survey-slow"), console, user);
            return true;
        }
        var recorded = _sectors.Survey(map, nearest.Id);
        _popup.PopupEntity(Loc.GetString(recorded ? "space-environment-survey-complete" : "space-environment-survey-recorded",
            ("kind", kind)), console, user);
        return recorded;
    }

    private bool TryEnvironment(EntityUid console, out SpaceEnvironmentComponent environment, out EntityUid map, out EntityUid grid)
    {
        environment = default!;
        map = default;
        grid = default;
        var xform = Transform(console);
        if (xform.MapUid is not { } mapUid || xform.GridUid is not { } gridUid
            || !TryComp<SpaceEnvironmentComponent>(mapUid, out var found)) return false;
        environment = found;
        map = mapUid;
        grid = gridUid;
        return true;
    }
}

[RegisterComponent]
public sealed partial class SpaceSurveyCooldownComponent : Component
{
    public TimeSpan ReadyAt;
}

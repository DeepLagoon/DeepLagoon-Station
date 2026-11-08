using Content.Server._NF.Shuttles.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map.Components;
using Content.Server.Shuttles.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

namespace Content.Server._NF.Shuttles.Systems;

public sealed partial class ForceAnchorSystem : EntitySystem
{
    [Dependency] PhysicsSystem _physics = default!;
    [Dependency] ShuttleSystem _shuttle = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ForceAnchorComponent, MapInitEvent>(OnForceAnchorMapInit);
        SubscribeLocalEvent<ForceAnchorPostFTLComponent, FTLCompletedEvent>(OnForceAnchorPostFTLCompleted);
        SubscribeLocalEvent<ConsoleFTLAttemptEvent>(OnConsoleFTLAttempt, before: new[] { typeof(ShuttleSystem) });
    }

    /// <summary>
    /// Prevents grids with ForceAnchor component from using FTL travel
    /// </summary>
    private void OnConsoleFTLAttempt(ref ConsoleFTLAttemptEvent args)
    {
        // Only proceed if this event isn't already cancelled
        if (args.Cancelled)
            return;

        // Check if the entity trying to FTL has a ForceAnchorComponent
        if (HasComp<ForceAnchorComponent>(args.Uid))
        {
            args.Cancelled = true;
            args.Reason = Loc.GetString("shuttle-console-force-anchored");
        }
    }

    public bool SetForceAnchored(EntityUid grid, bool anchored)
    {
        var wasAnchored = TryComp<ForceAnchorComponent>(grid, out var anchor);
        if (wasAnchored == anchored)
            return true;

        if (!TryComp<PhysicsComponent>(grid, out var body) || HasComp<FTLComponent>(grid))
            return false;

        if (anchored)
        {
            var bodyTypeBeforeAnchor = body.BodyType;
            var bodyStatusBeforeAnchor = body.BodyStatus;
            var fixedRotationBeforeAnchor = body.FixedRotation;
            var preserveAnchorLock = HasComp<PreventGridAnchorChangesComponent>(grid);
            anchor = AddComp<ForceAnchorComponent>(grid);
            anchor.PreviousBodyType = bodyTypeBeforeAnchor;
            anchor.PreviousBodyStatus = bodyStatusBeforeAnchor;
            anchor.PreviousFixedRotation = fixedRotationBeforeAnchor;
            anchor.PreserveAnchorLock = preserveAnchorLock;
            AnchorGrid(grid);
            return true;
        }

        if (!anchor!.PreserveAnchorLock &&
            (anchor.PreviousBodyType.HasValue || !HasComp<ForceAnchorPostFTLComponent>(grid)))
            RemComp<PreventGridAnchorChangesComponent>(grid);

        if (!HasComp<PreventGridAnchorChangesComponent>(grid))
        {
            if (anchor.PreviousBodyType is { } bodyType)
            {
                _physics.SetBodyType(grid, bodyType, body: body);
                _physics.SetBodyStatus(grid, body, anchor.PreviousBodyStatus);
                _physics.SetFixedRotation(grid, anchor.PreviousFixedRotation, body: body);
            }
            else if (!HasComp<MapComponent>(grid) && TryComp<ShuttleComponent>(grid, out var shuttle) && shuttle.Enabled)
            {
                _shuttle.Enable(grid, component: body, shuttle: shuttle);
            }
        }

        RemComp<ForceAnchorComponent>(grid);
        return true;
    }

    private void AnchorGrid(EntityUid grid)
    {
        _shuttle.Disable(grid, force: true);
        EnsureComp<PreventGridAnchorChangesComponent>(grid);
    }

    private void OnForceAnchorMapInit(Entity<ForceAnchorComponent> ent, ref MapInitEvent args)
    {
        AnchorGrid(ent);
    }

    private void OnForceAnchorPostFTLCompleted(Entity<ForceAnchorPostFTLComponent> ent, ref FTLCompletedEvent args)
    {
        if (TryComp<ForceAnchorComponent>(ent, out var anchor))
            anchor.PreserveAnchorLock = true;

        AnchorGrid(ent);
    }
}

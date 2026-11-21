using System.Numerics;
using Content.Server.Beam.Components;
using Content.Shared.Beam;
using Content.Shared.Beam.Components;
using Content.Shared.Physics;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Server.Beam;

public sealed partial class BeamSystem : SharedBeamSystem
{
    [Dependency] private FixtureSystem _fixture = default!;
    [Dependency] private TransformSystem _transform = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedBroadphaseSystem _broadphase = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<BeamComponent, CreateBeamSuccessEvent>(OnBeamCreationSuccess);
        SubscribeLocalEvent<BeamComponent, BeamControllerCreatedEvent>(OnControllerCreated);
        SubscribeLocalEvent<BeamComponent, BeamFiredEvent>(OnBeamFired);
        SubscribeLocalEvent<BeamComponent, ComponentRemove>(OnRemove);
    }

    private void OnBeamCreationSuccess(EntityUid uid, BeamComponent component, CreateBeamSuccessEvent args)
    {
        component.BeamShooter = args.User;
    }

    private void OnControllerCreated(EntityUid uid, BeamComponent component, BeamControllerCreatedEvent args)
    {
        component.OriginBeam = args.OriginBeam;
    }

    private void OnBeamFired(EntityUid uid, BeamComponent component, BeamFiredEvent args)
    {
        component.CreatedBeams.Add(args.CreatedBeam);
    }

    private void OnRemove(EntityUid uid, BeamComponent component, ComponentRemove args)
    {
        if (component.VirtualBeamController == null)
            return;

        if (component.CreatedBeams.Count == 0 && component.VirtualBeamController.Value.Valid)
            QueueDel(component.VirtualBeamController.Value);
    }

    /// <summary>
    /// If <see cref="TryCreateBeam"/> is successful, it spawns a beam from the user to the target.
    /// </summary>
    /// <param name="prototype">The prototype used to make the beam</param>
    /// <param name="userAngle">Angle of the user firing the beam</param>
    /// <param name="direction">The normalized direction from the user to the target.</param>
    /// <param name="beamStartPos">Where the beam will spawn in</param>
    /// <param name="distanceCorrection">Calculated correction so the <see cref="EdgeShape"/> can be properly dynamically created</param>
    /// <param name="controller"> The virtual beam controller that this beam will use. If one doesn't exist it will be created here.</param>
    /// <param name="bodyState">Optional sprite state for the <see cref="prototype"/> if it needs a dynamic one</param>
    /// <param name="shader">Optional shader for the <see cref="prototype"/> and <see cref="bodyState"/> if it needs something other than default</param>
    private bool CreateBeam(string prototype,
        Angle userAngle,
        Vector2 direction,
        MapCoordinates beamStartPos,
        Vector2 distanceCorrection,
        EntityUid? controller,
        string? bodyState = null,
        string shader = "unshaded")
    {
        var beamSpawnPos = beamStartPos;
        var ent = Spawn(prototype, beamSpawnPos);
        var shape = new EdgeShape(distanceCorrection, new Vector2(0,0));

        if (!TryComp<PhysicsComponent>(ent, out var physics) || !TryComp<BeamComponent>(ent, out var beam))
        {
            QueueDel(ent);
            return false;
        }

        FixturesComponent? manager = null;
        if (!_fixture.TryCreateFixture(
            ent,
            shape,
            "BeamBody",
            hard: false,
            collisionMask: (int)CollisionGroup.ItemMask,
            collisionLayer: (int)CollisionGroup.MobLayer,
            manager: manager,
            body: physics))
        {
            QueueDel(ent);
            return false;
        }

        _physics.SetBodyType(ent, BodyType.Dynamic, manager: manager, body: physics);
        _physics.SetCanCollide(ent, true, manager: manager, body: physics);
        _broadphase.RegenerateContacts((ent, physics, manager));

        var distanceLength = distanceCorrection.Length();

        var beamVisualizerEvent = new BeamVisualizerEvent(GetNetEntity(ent), distanceLength, userAngle, bodyState, shader);
        RaiseNetworkEvent(beamVisualizerEvent);

        if (controller != null)
            beam.VirtualBeamController = controller;

        else
        {
            var controllerEnt = Spawn("VirtualBeamEntityController", beamSpawnPos);
            beam.VirtualBeamController = controllerEnt;

            _audio.PlayPvs(beam.Sound, ent);

            var beamControllerCreatedEvent = new BeamControllerCreatedEvent(ent, controllerEnt);
            RaiseLocalEvent(controllerEnt, beamControllerCreatedEvent);
        }

        //Create the rest of the beam, sprites handled through the BeamVisualizerEvent
        for (var i = 0; i < distanceLength-1; i++)
        {
            beamSpawnPos = beamSpawnPos.Offset(direction);
            var newEnt = Spawn(prototype, beamSpawnPos);

            var ev = new BeamVisualizerEvent(GetNetEntity(newEnt), distanceLength, userAngle, bodyState, shader);
            RaiseNetworkEvent(ev);
        }

        var beamFiredEvent = new BeamFiredEvent(ent);
        RaiseLocalEvent(beam.VirtualBeamController.Value, beamFiredEvent);
        return true;
    }

    /// <summary>
    /// Called where you want an entity to create a beam from one target to another.
    /// Tries to create the beam and does calculations like the distance, angle, and offset.
    /// </summary>
    /// <param name="user">The entity that's firing off the beam</param>
    /// <param name="target">The entity that's being targeted by the user</param>
    /// <param name="bodyPrototype">The prototype spawned when this beam is created</param>
    /// <param name="bodyState">Optional sprite state for the <see cref="bodyPrototype"/> if a default one is not given</param>
    /// <param name="shader">Optional shader for the <see cref="bodyPrototype"/> if a default one is not given</param>
    /// <param name="controller"></param>
    public void TryCreateBeam(EntityUid user, EntityUid target, string bodyPrototype, string? bodyState = null, string shader = "unshaded", EntityUid? controller = null)
    {
        if (TerminatingOrDeleted(user) || TerminatingOrDeleted(target))
            return;

        var userMapPos = _transform.GetMapCoordinates(user);
        var targetMapPos = _transform.GetMapCoordinates(target);

        if (userMapPos.MapId == MapId.Nullspace || userMapPos.MapId != targetMapPos.MapId)
            return;

        var calculatedDistance = targetMapPos.Position - userMapPos.Position;
        var distance = calculatedDistance.Length();
        // Validate before normalization, angle conversion or spawning any entity.
        if (!float.IsFinite(distance) || distance <= 0)
            return;

        var direction = calculatedDistance / distance;
        var userAngle = direction.ToWorldAngle();
        var startOffset = direction;
        var distanceCorrection = calculatedDistance - startOffset;
        // A one-tile offset collapses the edge for adjacent tile centers. Use their midpoint
        // in that case; retain the existing collision span of every other beam.
        if (distanceCorrection == Vector2.Zero)
        {
            startOffset = calculatedDistance * .5f;
            distanceCorrection = calculatedDistance - startOffset;
        }
        var beamStartPos = userMapPos.Offset(startOffset);

        if (controller != null && TryComp<BeamComponent>(controller, out var controllerBeamComp))
        {
            controllerBeamComp.HitTargets.Add(user);
            controllerBeamComp.HitTargets.Add(target);
        }

        if (!CreateBeam(bodyPrototype, userAngle, direction, beamStartPos, distanceCorrection, controller, bodyState, shader))
            return;

        var ev = new CreateBeamSuccessEvent(user, target);
        RaiseLocalEvent(user, ev);
    }
}

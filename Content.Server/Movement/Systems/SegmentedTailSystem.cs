using System.Numerics;
using Content.Shared.Damage;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Server.Movement.Systems;

public sealed partial class SegmentedTailSystem : EntitySystem
{
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private SharedJointSystem _joints = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<SegmentedTailComponent, ComponentShutdown>(OnHeadShutdown);
        SubscribeLocalEvent<TailLinkComponent, ComponentShutdown>(OnLinkShutdown);
        SubscribeLocalEvent<TailLinkComponent, DamageChangedEvent>(OnLinkDamage);
    }

    public override void Update(float frameTime)
    {
        var heads = EntityQueryEnumerator<SegmentedTailComponent, PhysicsComponent, TransformComponent>();
        while (heads.MoveNext(out var uid, out var tail, out _, out var transform))
        {
            if (transform.MapUid is not { } map)
            {
                if (tail.Created)
                    RemoveChain((uid, tail));

                continue;
            }

            if (!tail.Created || tail.ChainMap != map)
            {
                if (tail.Created || !IsSavedChainValid((uid, tail), map))
                {
                    RemoveChain((uid, tail));
                    CreateChain((uid, tail), map);
                }

                tail.Created = true;
                tail.ChainMap = map;
            }

            var previous = uid;
            foreach (var segment in tail.Chain)
            {
                if (TerminatingOrDeleted(segment) || !TryComp<PhysicsComponent>(segment, out var body))
                    break;

                Follow(segment, previous, body, tail, frameTime);
                previous = segment;
            }
        }
    }

    private bool IsSavedChainValid(Entity<SegmentedTailComponent> head, EntityUid map)
    {
        if (head.Comp.Chain.Count != Math.Max(0, head.Comp.Count))
            return false;

        foreach (var segment in head.Comp.Chain)
        {
            if (TerminatingOrDeleted(segment) || !HasComp<PhysicsComponent>(segment) ||
                !TryComp<TailLinkComponent>(segment, out var link) || link.Head != head.Owner ||
                Transform(segment).MapUid != map)
            {
                return false;
            }
        }

        return true;
    }

    private void CreateChain(Entity<SegmentedTailComponent> head, EntityUid map)
    {
        var config = head.Comp;
        if (config.Spacing <= 0)
            return;

        var (position, angle) = _transform.GetWorldPositionRotation(head);
        var previous = head.Owner;
        for (var index = 1; index <= config.Count; index++)
        {
            var offset = angle.ToWorldVec() * (config.Spacing * index);
            var segment = Spawn(head.Comp.Segment, new EntityCoordinates(map, position - offset));
            if (!HasComp<PhysicsComponent>(segment))
            {
                QueueDel(segment);
                break;
            }

            _transform.SetWorldRotation(segment, angle);
            EnsureComp<TailLinkComponent>(segment).Head = head;
            config.Chain.Add(segment);
            var joint = _joints.CreateDistanceJoint(previous, segment, config.RearAnchor, config.FrontAnchor,
                minimumDistance: config.Spacing * 0.8f);
            joint.Length = config.Spacing;
            joint.MinLength = config.Spacing * 0.95f;
            joint.MaxLength = config.Spacing * 1.05f;
            joint.Stiffness = config.Stiffness;
            joint.Damping = config.Damping;
            previous = segment;
        }
    }

    private void Follow(EntityUid segment, EntityUid previous, PhysicsComponent body, SegmentedTailComponent tail, float dt)
    {
        var (target, facing) = _transform.GetWorldPositionRotation(previous);
        var (position, rotation) = _transform.GetWorldPositionRotation(segment);
        var separation = target - position;
        var distance = separation.Length();
        var direction = distance > 0.001f ? separation / distance : facing.ToWorldVec();
        var speed = Math.Max(0, tail.MaximumSpeed);
        var correction = direction * Math.Clamp((distance - tail.Spacing) * tail.FollowStrength, -speed, speed);
        var inheritedVelocity = _physics.GetMapLinearVelocity(segment, body) - body.LinearVelocity;
        var targetVelocity = _physics.GetMapLinearVelocity(previous) + correction - inheritedVelocity;
        var targetSpeedSquared = targetVelocity.LengthSquared();
        if (targetSpeedSquared > speed * speed)
            targetVelocity *= speed / MathF.Sqrt(targetSpeedSquared);

        var blend = Math.Clamp(dt * tail.Smoothing, 0f, 1f);
        var velocity = Vector2.Lerp(body.LinearVelocity, targetVelocity, blend);
        _physics.SetLinearVelocity(segment, velocity, body: body);

        if (tail.RotateSegments && distance > 0.001f)
        {
            var next = Angle.Lerp(rotation, direction.ToWorldAngle() + tail.SpriteRotation, blend);
            _transform.SetWorldRotation(segment, next);
        }
    }

    private void OnLinkDamage(EntityUid uid, TailLinkComponent link, DamageChangedEvent args)
    {
        if (args.DamageDelta is { } delta && !TerminatingOrDeleted(link.Head))
            _damage.TryChangeDamage(link.Head, delta, true, true, origin: args.Origin);
    }

    private void OnHeadShutdown(Entity<SegmentedTailComponent> head, ref ComponentShutdown args)
    {
        head.Comp.Disposing = true;
        RemoveChain(head);
    }

    private void RemoveChain(Entity<SegmentedTailComponent> head)
    {
        foreach (var segment in head.Comp.Chain)
        {
            if (TerminatingOrDeleted(segment) || !TryComp<TailLinkComponent>(segment, out var link) ||
                link.Head != head.Owner)
            {
                continue;
            }

            link.Head = EntityUid.Invalid;
            _joints.ClearJoints(segment);
            QueueDel(segment);
        }

        head.Comp.Chain.Clear();
        head.Comp.Created = false;
        head.Comp.ChainMap = null;
    }

    private void OnLinkShutdown(Entity<TailLinkComponent> segment, ref ComponentShutdown args)
    {
        var head = segment.Comp.Head;
        if (TryComp<SegmentedTailComponent>(head, out var tail) && !tail.Disposing && !TerminatingOrDeleted(head))
            QueueDel(head);
    }
}

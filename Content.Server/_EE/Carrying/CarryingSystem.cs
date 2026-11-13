using System.Numerics;
using Content.Server.DoAfter;
using Content.Server.Resist;
using Content.Server.Popups;
using Content.Server.Inventory;
using Content.Server.Nyanotrasen.Item.PseudoItem;
using Content.Shared.Mobs;
using Content.Shared.DoAfter;
using Content.Shared.Buckle.Components;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Hands;
using Content.Shared.Stunnable;
using Content.Shared.Interaction.Events;
using Content.Shared.Verbs;
using Content.Shared.Climbing.Events;
using Content.Shared.Carrying;
using Content.Shared.Contests;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.Standing;
using Content.Shared.ActionBlocker;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Item;
using Content.Shared.Throwing;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Events;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Mobs.Systems;
using Content.Shared.Nyanotrasen.Item.PseudoItem;
using Content.Shared.Storage;
using Content.Shared.Traits.Assorted; //Mono: Wheelchair user check
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Server.GameObjects;

namespace Content.Server.Carrying
{
    public sealed partial class CarryingSystem : EntitySystem
    {
        [Dependency] private VirtualItemSystem _virtualItemSystem = default!;
        [Dependency] private SharedHandsSystem _hands = default!;
        [Dependency] private CarryingSlowdownSystem _slowdown = default!;
        [Dependency] private DoAfterSystem _doAfterSystem = default!;
        [Dependency] private StandingStateSystem _standingState = default!;
        [Dependency] private ActionBlockerSystem _actionBlockerSystem = default!;
        [Dependency] private PullingSystem _pullingSystem = default!;
        [Dependency] private MobStateSystem _mobStateSystem = default!;
        [Dependency] private EscapeInventorySystem _escapeInventorySystem = default!;
        [Dependency] private PopupSystem _popupSystem = default!;
        [Dependency] private MovementSpeedModifierSystem _movementSpeed = default!;
        [Dependency] private PseudoItemSystem _pseudoItem = default!;
        [Dependency] private ContestsSystem _contests = default!;
        [Dependency] private TransformSystem _transform = default!;

        public const float BaseDistanceCoeff = 0.5f; // Frontier: default throwing speed reduction
        public const float MaxDistanceCoeff = 1.0f; // Frontier: default throwing speed reduction
        public const float DefaultMaxThrowDistance = 4.0f; // Frontier: maximum throwing distance
        private readonly List<(EntityUid Carrier, EntityUid Load)> _detachedLoads = new();

        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<CarriableComponent, GetVerbsEvent<AlternativeVerb>>(AddCarryVerb);
            SubscribeLocalEvent<CarryingComponent, GetVerbsEvent<InnateVerb>>(AddInsertCarriedVerb);
            SubscribeLocalEvent<CarryingComponent, VirtualItemDeletedEvent>(OnVirtualItemDeleted);
            SubscribeLocalEvent<CarryingComponent, BeforeThrowEvent>(OnThrow);
            SubscribeLocalEvent<CarryingComponent, EntParentChangedMessage>(OnParentChanged);
            SubscribeLocalEvent<CarryingComponent, MobStateChangedEvent>(OnMobStateChanged);
            SubscribeLocalEvent<CarryingComponent, EntityTerminatingEvent>(OnCarrierTerminating);
            SubscribeLocalEvent<BeingCarriedComponent, EntityTerminatingEvent>(OnLoadTerminating);
            SubscribeLocalEvent<BeingCarriedComponent, InteractionAttemptEvent>(OnInteractionAttempt);
            SubscribeLocalEvent<BeingCarriedComponent, MoveInputEvent>(OnMoveInput);
            SubscribeLocalEvent<BeingCarriedComponent, UpdateCanMoveEvent>(OnMoveAttempt);
            SubscribeLocalEvent<BeingCarriedComponent, StandAttemptEvent>(OnStandAttempt);
            SubscribeLocalEvent<BeingCarriedComponent, GettingInteractedWithAttemptEvent>(OnInteractedWith);
            SubscribeLocalEvent<BeingCarriedComponent, PullAttemptEvent>(OnPullAttempt);
            SubscribeLocalEvent<BeingCarriedComponent, StartClimbEvent>(OnStartClimb);
            SubscribeLocalEvent<BeingCarriedComponent, BuckledEvent>(OnBuckleChange);
            SubscribeLocalEvent<BeingCarriedComponent, UnbuckledEvent>(OnBuckleChange);
            SubscribeLocalEvent<BeingCarriedComponent, StrappedEvent>(OnBuckleChange);
            SubscribeLocalEvent<BeingCarriedComponent, UnstrappedEvent>(OnBuckleChange);
            SubscribeLocalEvent<CarriableComponent, CarryDoAfterEvent>(OnDoAfter);
        }

        private void AddCarryVerb(EntityUid uid, CarriableComponent component, GetVerbsEvent<AlternativeVerb> args)
        {
            if (!args.CanInteract || !args.CanAccess || !_mobStateSystem.IsAlive(args.User)
                || !CanCarry(args.User, uid, component)
                || HasComp<BeingCarriedComponent>(args.User) || HasComp<BeingCarriedComponent>(args.Target)
                || args.User == args.Target)
                return;

            AlternativeVerb verb = new()
            {
                Act = () =>
                {
                    StartCarryDoAfter(args.User, uid, component);
                },
                Text = Loc.GetString("carry-verb"),
                Priority = 2
            };
            args.Verbs.Add(verb);
        }

        private void AddInsertCarriedVerb(EntityUid uid, CarryingComponent component, GetVerbsEvent<InnateVerb> args)
        {
            // If the person is carrying someone, and the carried person is a pseudo-item, and the target entity is a storage,
            // then add an action to insert the carried entity into the target
            var toInsert = args.Using;
            if (toInsert is not { Valid: true } || !component.Loads.Contains(toInsert.Value) || !args.CanAccess
                || !TryComp<PseudoItemComponent>(toInsert, out var pseudoItem)
                || !TryComp<StorageComponent>(args.Target, out var storageComp)
                || !_pseudoItem.CheckItemFits((toInsert.Value, pseudoItem), (args.Target, storageComp)))
                return;

            InnateVerb verb = new()
            {
                Act = () =>
                {
                    if (!component.Loads.Contains(toInsert.Value) || TerminatingOrDeleted(args.Target) ||
                        !_pseudoItem.CheckItemFits((toInsert.Value, pseudoItem), (args.Target, storageComp)))
                        return;

                    DropCarried(uid, toInsert.Value);
                    _pseudoItem.TryInsert(args.Target, toInsert.Value, pseudoItem, storageComp);
                },
                Text = Loc.GetString("action-name-insert-other", ("target", toInsert)),
                Priority = 2
            };
            args.Verbs.Add(verb);
        }

        /// <summary>
        /// Deleting one occupied hand slot releases the load associated with it.
        /// </summary>
        private void OnVirtualItemDeleted(EntityUid uid, CarryingComponent component, VirtualItemDeletedEvent args)
        {
            if (!component.Loads.Contains(args.BlockingEntity))
                return;

            DropCarried(uid, args.BlockingEntity);
        }

        /// <summary>
        /// Basically using virtual item passthrough to throw the carried person. A new age!
        /// Maybe other things besides throwing should use virt items like this...
        /// </summary>
        private void OnThrow(EntityUid uid, CarryingComponent component, ref BeforeThrowEvent args)
        {
            if (!TryComp<VirtualItemComponent>(args.ItemUid, out var virtItem)
                || !component.Loads.Contains(virtItem.BlockingEntity))
                return;

            args.ItemUid = virtItem.BlockingEntity;

            var contestCoeff = _contests.MassContest(uid, virtItem.BlockingEntity, false, 2f) // Frontier: "args.throwSpeed *="<"var contestCoeff ="
                                * _contests.StaminaContest(uid, virtItem.BlockingEntity);

            // Frontier: sanitize our range regardless of CVar values - TODO: variable throw distance ranges (via traits, etc.)
            contestCoeff = float.Min(BaseDistanceCoeff * contestCoeff, MaxDistanceCoeff);
            if (args.Direction.Length() > DefaultMaxThrowDistance * contestCoeff)
                args.Direction = args.Direction.Normalized() * DefaultMaxThrowDistance * contestCoeff;
            // End Frontier
        }

        private void OnParentChanged(EntityUid uid, CarryingComponent component, ref EntParentChangedMessage args)
        {
            var xform = args.Transform;
            if (xform.ParentUid == xform.GridUid || xform.ParentUid == xform.MapUid)
                return;

            ReleaseLoads(uid, component);
        }

        private void OnMobStateChanged(EntityUid uid, CarryingComponent component, MobStateChangedEvent args)
        {
            if (args.NewMobState != MobState.Alive)
                ReleaseLoads(uid, component);
        }

        private void OnCarrierTerminating(Entity<CarryingComponent> carrier, ref EntityTerminatingEvent args)
            => ReleaseLoads(carrier, carrier.Comp);

        private void OnLoadTerminating(Entity<BeingCarriedComponent> load, ref EntityTerminatingEvent args)
            => DropCarried(load.Comp.Carrier, load);

        private void ReleaseLoads(EntityUid carrier, CarryingComponent component)
        {
            foreach (var load in new List<EntityUid>(component.Loads))
                DropCarried(carrier, load);
        }

        /// <summary>
        /// Only let the person being carried interact with their carrier and things on their person.
        /// </summary>
        private void OnInteractionAttempt(EntityUid uid, BeingCarriedComponent component, InteractionAttemptEvent args)
        {
            if (args.Target == null)
                return;

            var targetParent = Transform(args.Target.Value).ParentUid;

            if (args.Target.Value != component.Carrier && targetParent != component.Carrier && targetParent != uid)
                args.Cancelled = true;
        }

        /// <summary>
        /// Try to escape via the escape inventory system.
        /// </summary>
        private void OnMoveInput(EntityUid uid, BeingCarriedComponent component, ref MoveInputEvent args)
        {
            if (!TryComp<CanEscapeInventoryComponent>(uid, out var escape)
                || !args.HasDirectionalMovement)
                return;

            // Check if the victim is in any way incapacitated, and if not make an escape attempt.
            // Escape time scales with the inverse of a mass contest. Being lighter makes escape harder.
            if (_actionBlockerSystem.CanInteract(uid, component.Carrier))
            {
                var disadvantage = _contests.MassContest(component.Carrier, uid, false, 2f);
                _escapeInventorySystem.AttemptEscape(uid, component.Carrier, escape, disadvantage);
            }
        }

        private void OnMoveAttempt(EntityUid uid, BeingCarriedComponent component, UpdateCanMoveEvent args)
        {
            args.Cancel();
        }

        private void OnStandAttempt(EntityUid uid, BeingCarriedComponent component, StandAttemptEvent args)
        {
            args.Cancel();
        }

        private void OnInteractedWith(EntityUid uid, BeingCarriedComponent component, GettingInteractedWithAttemptEvent args)
        {
            if (args.Uid != component.Carrier)
                args.Cancelled = true;
        }

        private void OnPullAttempt(EntityUid uid, BeingCarriedComponent component, PullAttemptEvent args)
        {
            args.Cancelled = true;
        }

        private void OnStartClimb(EntityUid uid, BeingCarriedComponent component, ref StartClimbEvent args)
        {
            DropCarried(component.Carrier, uid);
        }

        private void OnBuckleChange<TEvent>(EntityUid uid, BeingCarriedComponent component, TEvent args)
        {
            DropCarried(component.Carrier, uid);
        }

        private void OnDoAfter(EntityUid uid, CarriableComponent component, CarryDoAfterEvent args)
        {
            component.PickupPending = false;
            if (args.Handled || args.Cancelled || !_mobStateSystem.IsAlive(args.Args.User)
                || !CanCarry(args.Args.User, uid, component))
                return;

            args.Handled = Carry(args.Args.User, uid, component.FreeHandsRequired);
        }
        private void StartCarryDoAfter(EntityUid carrier, EntityUid carried, CarriableComponent component)
        {
            if (!CanCarry(carrier, carried, component))
                return;

            if (!TryComp<PhysicsComponent>(carrier, out var carrierPhysics)
                || !TryComp<PhysicsComponent>(carried, out var carriedPhysics)
                || carriedPhysics.Mass > carrierPhysics.Mass * 2f)
            {
                _popupSystem.PopupEntity(Loc.GetString("carry-too-heavy"), carried, carrier, Shared.Popups.PopupType.SmallCaution);
                return;
            }

            var length = component.PickupDuration // Frontier: removed outer TimeSpan.FromSeconds()
                        * _contests.MassContest(carriedPhysics, carrierPhysics, false, 4f)
                        * _contests.StaminaContest(carrier, carried)
                        * (_standingState.IsDown(carried) ? 0.5f : 1);

            // Frontier: sanitize pickup time duration regardless of CVars - no near-instant pickups.
            var duration = TimeSpan.FromSeconds(
                float.Clamp(length,
                component.MinPickupDuration,
                component.MaxPickupDuration));
            // End Frontier

            var ev = new CarryDoAfterEvent();
            var args = new DoAfterArgs(EntityManager, carrier, duration, ev, carried, target: carried) // Frontier: length<duration
            {
                BreakOnMove = true,
                NeedHand = true,
                MultiplyDelay = false, // Goobstation
            };

            if (!_doAfterSystem.TryStartDoAfter(args))
                return;

            component.PickupPending = true;

            // Show a popup to the person getting picked up
            _popupSystem.PopupEntity(Loc.GetString("carry-started", ("carrier", carrier)), carried, carried);
        }

        private bool Carry(EntityUid carrier, EntityUid carried, int hands)
        {
            if (TryComp<PullableComponent>(carried, out var pullable) &&
                !_pullingSystem.TryStopPull(carried, pullable))
            {
                return false;
            }

            for (var i = 0; i < hands; i++)
            {
                if (_virtualItemSystem.TrySpawnVirtualItemInHand(carried, carrier, out var item) &&
                    _hands.IsHolding(carrier, item))
                {
                    continue;
                }

                if (item is { } orphan)
                    QueueDel(orphan);
                _virtualItemSystem.DeleteInHandsMatching(carrier, carried);
                return false;
            }

            _transform.AttachToGridOrMap(carrier);
            _transform.AttachToGridOrMap(carried);
            _transform.SetCoordinates(carried, Transform(carrier).Coordinates);
            _transform.SetParent(carried, carrier);

            var carryingComp = EnsureComp<CarryingComponent>(carrier);
            carryingComp.Loads.Add(carried);
            ApplyCarrySlowdown(carrier, carryingComp);
            var carriedComp = EnsureComp<BeingCarriedComponent>(carried);
            EnsureComp<KnockedDownComponent>(carried);

            carriedComp.Carrier = carrier;

            _actionBlockerSystem.UpdateCanMove(carried);
            return true;
        }

        public bool TryCarry(EntityUid carrier, EntityUid toCarry, CarriableComponent? carriedComp = null)
        {
            if (!Resolve(toCarry, ref carriedComp, false)
                || !CanCarry(carrier, toCarry, carriedComp)
                || HasComp<BeingCarriedComponent>(carrier)
                || HasComp<ItemComponent>(carrier)
                || TryComp<PhysicsComponent>(carrier, out var carrierPhysics)
                && TryComp<PhysicsComponent>(toCarry, out var toCarryPhysics)
                && carrierPhysics.Mass < toCarryPhysics.Mass * 2f)
                return false;

            return Carry(carrier, toCarry, carriedComp.FreeHandsRequired);
        }

        public void DropCarried(EntityUid carrier, EntityUid carried)
        {
            if (TryComp<CarryingComponent>(carrier, out var carrying) && carrying.Loads.Remove(carried))
            {
                _virtualItemSystem.DeleteInHandsMatching(carrier, carried);
                if (carrying.Loads.Count == 0)
                {
                    RemComp<CarryingComponent>(carrier);
                    RemComp<CarryingSlowdownComponent>(carrier);
                }
                else if (!TerminatingOrDeleted(carrier))
                    ApplyCarrySlowdown(carrier, carrying);

                if (!TerminatingOrDeleted(carrier))
                    _movementSpeed.RefreshMovementSpeedModifiers(carrier);
            }

            if (TerminatingOrDeleted(carried) || !TryComp<BeingCarriedComponent>(carried, out var load) ||
                load.Carrier != carrier)
                return;

            RemComp<BeingCarriedComponent>(carried);
            RemComp<KnockedDownComponent>(carried);
            if (Transform(carried).ParentUid == carrier)
                _transform.AttachToGridOrMap(carried);
            _actionBlockerSystem.UpdateCanMove(carried);
            if (!HasComp<LegsParalyzedComponent>(carried))
                _standingState.Stand(carried);
        }

        private void ApplyCarrySlowdown(EntityUid carrier, CarryingComponent carrying)
        {
            var modifier = 1f;
            foreach (var load in carrying.Loads)
            {
                if (TerminatingOrDeleted(load))
                    continue;

                var ratio = _contests.MassContest(carrier, load, true);
                modifier *= Math.Max(0.1f, 1 - 0.15f / (ratio * ratio));
            }

            var slowdownComp = EnsureComp<CarryingSlowdownComponent>(carrier);
            _slowdown.SetModifier(carrier, modifier, modifier, slowdownComp);
        }

        public bool CanCarry(EntityUid carrier, EntityUid carried, CarriableComponent? carriedComp = null)
        {
            if (!Resolve(carried, ref carriedComp, false)
                || carriedComp.PickupPending
                || carrier == carried || TerminatingOrDeleted(carried) || TerminatingOrDeleted(carrier)
                || !HasComp<MapGridComponent>(Transform(carrier).ParentUid)
                || HasComp<BeingCarriedComponent>(carrier)
                || HasComp<BeingCarriedComponent>(carried)
                || !TryComp<HandsComponent>(carrier, out var hands)
                || hands.CountFreeHands() < carriedComp.FreeHandsRequired)
                return false;

            return true;
        }

        public override void Update(float frameTime)
        {
            _detachedLoads.Clear();
            var query = EntityQueryEnumerator<BeingCarriedComponent>();
            while (query.MoveNext(out var carried, out var comp))
            {
                var carrier = comp.Carrier;
                // SOMETIMES - when an entity is inserted into disposals, or a cryosleep chamber - it can get re-parented without a proper reparent event
                // when this happens, it needs to be dropped because it leads to weird behavior
                if (TerminatingOrDeleted(carrier) || Transform(carried).ParentUid != carrier)
                {
                    _detachedLoads.Add((carrier, carried));
                    continue;
                }

                // Make sure the carried entity is always centered relative to the carrier, as gravity pulls can offset it otherwise
                var xform = Transform(carried);
                if (!xform.LocalPosition.Equals(Vector2.Zero))
                {
                    xform.LocalPosition = Vector2.Zero;
                }
            }
            query.Dispose();
            foreach (var (carrier, load) in _detachedLoads)
                DropCarried(carrier, load);
        }
    }
}

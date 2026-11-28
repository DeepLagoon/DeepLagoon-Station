using Content.Server.Cargo.Systems;
using Content.Server._DeepLagoon.Administration;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Shared.Verbs;
using JetBrains.Annotations;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server.Stack
{
    /// <summary>
    ///     Entity system that handles everything relating to stacks.
    ///     This is a good example for learning how to code in an ECS manner.
    /// </summary>
    [UsedImplicitly]
    public sealed partial class StackSystem : SharedStackSystem
    {
        [Dependency] private IPrototypeManager _prototypeManager = default!;
        [Dependency] private AdminCashProvenanceSystem _cashProvenance = default!;
        [Dependency] private PricingSystem _pricing = default!;
        [Dependency] private Content.Server._Mono.Cargo.DriftingPriceSystem _driftingPrices = default!;
        [Dependency] private Content.Server._DeepLagoon.CryptoFarm.CryptoFarmSystem _cryptoFarms = default!;
        [Dependency] private SharedUserInterfaceSystem _ui = default!; // Cherry-picked from space-station-14#32938 courtesy of Ilya246

        public static readonly int[] DefaultSplitAmounts = { 1, 5, 10, 20, 50, 100, 500, 1000, 5000, 10000 };

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<StackComponent, GetVerbsEvent<AlternativeVerb>>(OnStackAlternativeInteract);
        }

        public override void SetCount(EntityUid uid, int amount, StackComponent? component = null)
        {
            if (!Resolve(uid, ref component, false))
                return;

            var oldCount = component.Count;
            base.SetCount(uid, amount, component);
            if (component.Count < oldCount)
                ConsumeCashOrigins(uid, oldCount - component.Count);

            // Queue delete stack if count reaches zero.
            if (component.Count <= 0 && !component.Lingering)
                QueueDel(uid);
        }

        /// <summary>
        ///     Try to split this stack into two. Returns a non-null <see cref="Robust.Shared.GameObjects.EntityUid"/> if successful.
        /// </summary>
        public override EntityUid? Split(EntityUid uid, int amount, EntityCoordinates spawnPosition, StackComponent? stack = null) // Goobstation - override virtual method
        {
            if (!Resolve(uid, ref stack))
                return null;

            if (amount <= 0 || amount > stack.Count)
                return null;
            var cashOrigins = stack.Unlimited ? null : CashOrigins(uid, amount);

            // Try to remove the amount of things we want to split from the original stack...
            if (!Use(uid, amount, stack))
                return null;

            // Get a prototype ID to spawn the new entity. Null is also valid, although it should rarely be picked...
            var prototype = _prototypeManager.TryIndex<StackPrototype>(stack.StackTypeId, out var stackType)
                ? stackType.Spawn.ToString()
                : Prototype(uid)?.ID;

            // Set the output parameter in the event instance to the newly split stack.
            var entity = Spawn(prototype, spawnPosition);

            if (TryComp(entity, out StackComponent? stackComp))
            {
                // Set the split stack's count.
                SetCount(entity, amount, stackComp);
                // Don't let people dupe unlimited stacks
                stackComp.Unlimited = false;
            }

            if (_pricing.GetCraftedUnitLimit(uid) is { } unitLimit)
                _pricing.MarkCraftedProduct(entity, unitLimit);
            AppendCashOrigins(entity, cashOrigins);

            var ev = new StackSplitEvent(entity);
            RaiseLocalEvent(uid, ref ev);

            return entity;
        }

        protected override bool CanCombineStacks(EntityUid donor, EntityUid recipient)
        {
            return _pricing.GetCraftedUnitLimit(donor) == _pricing.GetCraftedUnitLimit(recipient)
                && _cryptoFarms.CanMergeInto(donor, recipient);
        }

        protected override void TransferUnits(EntityUid donor, StackComponent donorStack,
            EntityUid recipient, StackComponent recipientStack, int amount)
        {
            var origins = CashOrigins(donor, amount);
            _driftingPrices.MergeUnitPrice(donor, recipient, recipientStack.Count, amount);
            base.TransferUnits(donor, donorStack, recipient, recipientStack, amount);
            AppendCashOrigins(recipient, origins);
        }

        // Mixed stacks consume tracked units first, in insertion order.
        internal List<AdminCashPortion>? CashOrigins(EntityUid uid, int amount)
        {
            if (amount <= 0 || _cashProvenance.GetProvenance(uid) is not { } provenance)
                return null;
            var result = new List<AdminCashPortion>();
            foreach (var part in provenance.Portions)
            {
                var units = Math.Min(amount, part.Units);
                result.Add(new AdminCashPortion { Spawner = part.Spawner, Name = part.Name, Units = units });
                amount -= units;
                if (amount == 0)
                    break;
            }
            return result;
        }

        private void AppendCashOrigins(EntityUid uid, List<AdminCashPortion>? origins)
        {
            if (origins is { Count: > 0 })
                _cashProvenance.EnsureProvenance(uid).Portions.AddRange(origins);
        }

        private void ConsumeCashOrigins(EntityUid uid, int amount)
        {
            if (_cashProvenance.GetProvenance(uid) is not { } provenance)
                return;
            var portions = provenance.Portions;
            var emptied = 0;
            foreach (var part in portions)
            {
                var taken = Math.Min(amount, part.Units);
                part.Units -= taken;
                amount -= taken;
                if (part.Units == 0)
                    emptied++;
                if (amount == 0)
                    break;
            }
            portions.RemoveRange(0, emptied);
            if (portions.Count == 0)
                RemComp<AdminCashProvenanceComponent>(uid);
        }

        /// <summary>
        ///     Spawns a stack of a certain stack type. See <see cref="StackPrototype"/>.
        /// </summary>
        public EntityUid Spawn(int amount, ProtoId<StackPrototype> id, EntityCoordinates spawnPosition)
        {
            var proto = _prototypeManager.Index(id);
            return Spawn(amount, proto, spawnPosition);
        }

        /// <summary>
        ///     Spawns a stack of a certain stack type. See <see cref="StackPrototype"/>.
        /// </summary>
        public EntityUid Spawn(int amount, StackPrototype prototype, EntityCoordinates spawnPosition)
        {
            // Set the output result parameter to the new stack entity...
            var entity = Spawn(prototype.Spawn, spawnPosition);
            var stack = Comp<StackComponent>(entity);

            // And finally, set the correct amount!
            SetCount(entity, amount, stack);
            return entity;
        }

        /// <summary>
        ///     Say you want to spawn 97 units of something that has a max stack count of 30.
        ///     This would spawn 3 stacks of 30 and 1 stack of 7.
        /// </summary>
        public List<EntityUid> SpawnMultiple(string entityPrototype, int amount, EntityCoordinates spawnPosition)
        {
            if (amount <= 0)
            {
                Log.Error(
                    $"Attempted to spawn an invalid stack: {entityPrototype}, {amount}. Trace: {Environment.StackTrace}");
                return new();
            }

            var spawns = CalculateSpawns(entityPrototype, amount);

            var spawnedEnts = new List<EntityUid>();
            foreach (var count in spawns)
                spawnedEnts.Add(SpawnStackAtPosition(entityPrototype, count, spawnPosition));

            return spawnedEnts;
        }

        internal EntityUid SpawnStackAtPosition(EntProtoId prototype, int count, EntityCoordinates position)
        {
            var entity = SpawnAtPosition(prototype, position);
            SetCount(entity, count);
            return entity;
        }

        /// <inheritdoc cref="SpawnMultiple(string,int,EntityCoordinates)"/>
        public List<EntityUid> SpawnMultiple(string entityPrototype, int amount, EntityUid target)
        {
            if (amount <= 0)
            {
                Log.Error(
                    $"Attempted to spawn an invalid stack: {entityPrototype}, {amount}. Trace: {Environment.StackTrace}");
                return new();
            }

            var spawns = CalculateSpawns(entityPrototype, amount);

            var spawnedEnts = new List<EntityUid>();
            foreach (var count in spawns)
            {
                var entity = SpawnNextToOrDrop(entityPrototype, target);
                spawnedEnts.Add(entity);
                SetCount(entity, count);
            }

            return spawnedEnts;
        }

        /// <summary>
        /// Calculates how many stacks to spawn that total up to <paramref name="amount"/>.
        /// </summary>
        /// <param name="entityPrototype">The stack to spawn.</param>
        /// <param name="amount">The amount of pieces across all stacks.</param>
        /// <returns>The list of stack counts per entity.</returns>
        private List<int> CalculateSpawns(string entityPrototype, int amount)
        {
            var proto = _prototypeManager.Index<EntityPrototype>(entityPrototype);
            proto.TryComp<StackComponent>(out var stack, EntityManager.ComponentFactory);
            var maxCountPerStack = GetMaxCount(stack);
            var amounts = new List<int>();
            while (amount > 0)
            {
                var countAmount = Math.Min(maxCountPerStack, amount);
                amount -= countAmount;
                amounts.Add(countAmount);
            }

            return amounts;
        }

        private void OnStackAlternativeInteract(EntityUid uid, StackComponent stack, GetVerbsEvent<AlternativeVerb> args)
        {
            if (!args.CanAccess || !args.CanInteract || args.Hands == null || stack.Count == 1)
                return;

            // Frontier: cherry-picked from ss14#32938, moved up top
            var priority = 1;
            if (_ui.HasUi(uid, StackCustomSplitUiKey.Key)) // Frontier: check for interface
            {
                AlternativeVerb custom = new()
                {
                    Text = Loc.GetString("comp-stack-split-custom"),
                    Category = VerbCategory.Split,
                    Act = () =>
                    {
                        _ui.OpenUi(uid, StackCustomSplitUiKey.Key, args.User);
                    },
                    Priority = priority--
                };
                args.Verbs.Add(custom);
            }
            // End Frontier: cherry-picked from ss14#32938, moved up top

            AlternativeVerb halve = new()
            {
                Text = Loc.GetString("comp-stack-split-halve"),
                Category = VerbCategory.Split,
                Act = () => UserSplit(uid, args.User, stack.Count / 2, stack),
                Priority = priority-- // Frontier: 1<priority--
            };
            args.Verbs.Add(halve);

            foreach (var amount in DefaultSplitAmounts)
            {
                if (amount >= stack.Count)
                    continue;

                AlternativeVerb verb = new()
                {
                    Text = amount.ToString(),
                    Category = VerbCategory.Split,
                    Act = () => UserSplit(uid, args.User, amount, stack),
                    // we want to sort by size, not alphabetically by the verb text.
                    Priority = priority
                };

                priority--;

                args.Verbs.Add(verb);
            }
        }

        // Cherry-picked from ss14#32938 courtesy of Ilya246
        protected override void OnCustomSplitMessage(Entity<StackComponent> ent, ref StackCustomSplitAmountMessage message)
        {
            var (uid, comp) = ent;

            // digital ghosts shouldn't be allowed to split stacks
            if (!(message.Actor is { Valid: true } user))
                return;

            var amount = message.Amount;
            UserSplit(uid, user, amount, comp);
        }
        // End cherry-pick from ss14#32938 courtesy of Ilya246

        private void UserSplit(EntityUid uid, EntityUid userUid, int amount,
            StackComponent? stack = null,
            TransformComponent? userTransform = null)
        {
            if (!Resolve(uid, ref stack))
                return;

            if (!Resolve(userUid, ref userTransform))
                return;

            if (amount <= 0)
            {
                Popup.PopupCursor(Loc.GetString("comp-stack-split-too-small"), userUid, PopupType.Medium);
                return;
            }

            if (Split(uid, amount, userTransform.Coordinates, stack) is not {} split)
                return;

            Hands.PickupOrDrop(userUid, split);

            Popup.PopupCursor(Loc.GetString("comp-stack-split"), userUid);
        }
    }
}

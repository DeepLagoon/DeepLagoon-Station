using System.Collections.Generic; // Mono
using Content.Server.Advertise.Components;
using Content.Server.Chat.Systems;
using Content.Server.Power.Components;
using Content.Shared.Chat; // Einstein Engines - Languages
using Content.Shared.Dataset;
using Content.Shared.VendingMachines;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.Advertise.EntitySystems;

// Mono - update delay replaced with priority queue
public sealed partial class AdvertiseSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private IGameTiming _gameTiming = default!;
    [Dependency] private ChatSystem _chat = default!;

    // Mono
    private PriorityQueue<EntityUid, TimeSpan> _advertQueue = new();

    // Mono - cache dataset protos for performance reasons
    private Dictionary<ProtoId<LocalizedDatasetPrototype>, LocalizedDatasetPrototype> _cachedDatasets = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<AdvertiseComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<AdvertiseComponent, EntityPausedEvent>(OnPaused);
        SubscribeLocalEvent<AdvertiseComponent, EntityUnpausedEvent>(OnUnpaused);

        SubscribeLocalEvent<ApcPowerReceiverComponent, AttemptAdvertiseEvent>(OnPowerReceiverAttemptAdvertiseEvent);
        SubscribeLocalEvent<VendingMachineComponent, AttemptAdvertiseEvent>(OnVendingAttemptAdvertiseEvent);

        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnProtoReload); // Mono
    }

    // Mono - has to be ComponentInit so it doesn't break when loading again after MapInit
    // Mono - ComponentInit component modifications is a nono, back to mapInit with a check
    private void OnMapInit(EntityUid uid, AdvertiseComponent advert, MapInitEvent args)
    {
        if (advert.AdvertisementInitialized) return;
        advert.AdvertisementInitialized = true;
        if (advert.PausedAdvertisementDelay is {} remaining)
        {
            advert.NextAdvertisementTime = _gameTiming.CurTime + remaining;
            _advertQueue.Enqueue(uid, advert.NextAdvertisementTime);
        }
        else if (advert.NextAdvertisementTime == TimeSpan.Zero)
        {
            var prewarm = advert.Prewarm;
            RandomizeNextAdvertTime(uid, advert, prewarm);
        }
        else
        {
            _advertQueue.Enqueue(uid, advert.NextAdvertisementTime);
        }
        advert.PausedAdvertisementDelay = IsPaused(uid)
            ? RemainingDelay(advert) : null;
    }

    private TimeSpan RemainingDelay(AdvertiseComponent advert)
    {
        var remaining = advert.NextAdvertisementTime - _gameTiming.CurTime;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void OnPaused(Entity<AdvertiseComponent> ent, ref EntityPausedEvent args)
    {
        if (ent.Comp.AdvertisementInitialized)
            ent.Comp.PausedAdvertisementDelay = RemainingDelay(ent.Comp);
    }

    private void OnUnpaused(Entity<AdvertiseComponent> ent, ref EntityUnpausedEvent args)
    {
        if (!ent.Comp.AdvertisementInitialized) return;
        // Preserve the remaining delay, rather than advertising immediately or catching up.
        ent.Comp.NextAdvertisementTime = ent.Comp.PausedAdvertisementDelay is {} remaining
            ? _gameTiming.CurTime + remaining : ent.Comp.NextAdvertisementTime + args.PausedTime;
        ent.Comp.PausedAdvertisementDelay = null;
        _advertQueue.Enqueue(ent.Owner, ent.Comp.NextAdvertisementTime);
    }

    private void RandomizeNextAdvertTime(EntityUid uid, AdvertiseComponent advert, bool prewarm = false)
    {
        var minDuration = prewarm ? 0 : Math.Max(1, advert.MinimumWait);
        var maxDuration = Math.Max(minDuration, advert.MaximumWait);
        var waitDuration = TimeSpan.FromSeconds(_random.Next(minDuration, maxDuration));

        advert.NextAdvertisementTime = _gameTiming.CurTime + waitDuration;
        _advertQueue.Enqueue(uid, advert.NextAdvertisementTime);
    }

    // Mono
    private void OnProtoReload(PrototypesReloadedEventArgs ev)
    {
        _cachedDatasets.Clear();
    }

    public void SayAdvertisement(EntityUid uid, AdvertiseComponent? advert = null)
    {
        if (!Resolve(uid, ref advert) || IsPaused(uid) || EntityManager.IsQueuedForDeletion(uid))
            return;

        var attemptEvent = new AttemptAdvertiseEvent(uid);
        RaiseLocalEvent(uid, ref attemptEvent);
        if (attemptEvent.Cancelled)
            return;

        // Mono
        if (!_cachedDatasets.ContainsKey(advert.Pack))
        {
            if (!_prototypeManager.TryIndex(advert.Pack, out var advertisements))
                return;

            _cachedDatasets[advert.Pack] = advertisements;
        }

        // Mono
        var adverts = _cachedDatasets[advert.Pack];
        // TODO: investigate why TrySendInGameICMessage takes entire milliseconds
        _chat.TrySendInGameICMessage(uid, Loc.GetString(_random.Pick(adverts.Values)), InGameICChatType.Speak, hideChat: true);
    }

    public override void Update(float frameTime)
    {
        // Stale entries from deletion, rescheduling or unpausing are discarded within a fixed budget.
        var budget = Math.Min(_advertQueue.Count, 256);
        while (budget-- > 0)
        {
            if (!_advertQueue.TryPeek(out var uid, out var time))
                break;
            if (TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid) || !TryComp<AdvertiseComponent>(uid, out var advertise) || advertise.NextAdvertisementTime != time)
            {
                _advertQueue.Dequeue();
                continue;
            }

            // it's a priority queue so everything after this will be later
            if (advertise.NextAdvertisementTime > _gameTiming.CurTime)
                break;

            _advertQueue.Dequeue();
            // A custom priority queue bypasses ECS paused-query filtering. Do not spawn speech
            // or Audio on sleeping maps; OnUnpaused re-enqueues the shifted deadline.
            if (IsPaused(uid))
                continue;
            SayAdvertisement(uid, advertise);
            RandomizeNextAdvertTime(uid, advertise);
        }
    }

    public override void Shutdown()
    {
        _advertQueue.Clear();
        _cachedDatasets.Clear();
        base.Shutdown();
    }


    private static void OnPowerReceiverAttemptAdvertiseEvent(EntityUid uid, ApcPowerReceiverComponent powerReceiver, ref AttemptAdvertiseEvent args)
    {
        args.Cancelled |= !powerReceiver.Powered;
    }

    private static void OnVendingAttemptAdvertiseEvent(EntityUid uid, VendingMachineComponent machine, ref AttemptAdvertiseEvent args)
    {
        args.Cancelled |= machine.Broken;
    }
}

[ByRefEvent]
public record struct AttemptAdvertiseEvent(EntityUid? Advertiser)
{
    public bool Cancelled = false;
}

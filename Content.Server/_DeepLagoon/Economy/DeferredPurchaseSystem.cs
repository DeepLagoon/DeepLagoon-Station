using System.Linq;
using Content.Shared.GameTicking;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Economy;

/// <summary>A short catalogue refresh delay. No money or goods are reserved before execution.</summary>
public sealed partial class DeferredPurchaseSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    private readonly record struct Key(EntityUid Console, EntityUid Buyer, string Purchase);
    private sealed record Pending(TimeSpan Due, ICommonSession Session, Enum UiKey, Func<bool> Valid, Action Execute);
    private readonly Dictionary<Key, Pending> _pending = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _pending.Clear());
    }

    public bool IsPending(EntityUid console, EntityUid buyer, string purchase)
        => _pending.ContainsKey(new(console, buyer, purchase));

    public void Enqueue(EntityUid console, EntityUid buyer, string purchase, Enum uiKey, Func<bool> valid, Action execute)
    {
        var key = new Key(console, buyer, purchase);
        if (_pending.ContainsKey(key) || !TryComp<ActorComponent>(buyer, out var actor)) return;
        _pending.Add(key, new(_timing.CurTime + TimeSpan.FromSeconds(1), actor.PlayerSession, uiKey, valid, execute));
    }

    public override void Update(float frameTime)
    {
        foreach (var (key, work) in _pending.ToArray())
        {
            if (_timing.CurTime < work.Due) continue;
            _pending.Remove(key);
            // Closing the console, changing bodies or removing the order cancels an unpaid request.
            if (!Exists(key.Console) || !Exists(key.Buyer) || work.Session.AttachedEntity != key.Buyer ||
                !TryComp<ActorComponent>(key.Buyer, out var actor) || actor.PlayerSession != work.Session ||
                !_ui.IsUiOpen(key.Console, work.UiKey, key.Buyer) || !work.Valid()) continue;
            work.Execute();
        }
    }

    public override void Shutdown()
    {
        _pending.Clear();
        base.Shutdown();
    }
}

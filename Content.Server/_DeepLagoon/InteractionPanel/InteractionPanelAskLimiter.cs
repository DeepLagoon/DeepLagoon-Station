using System.Linq;
namespace Content.Server._DeepLagoon.InteractionPanel;

// По аккаунтам, а не по мобам: пересоздание персонажа/панели не обходит защиту.
public sealed class InteractionPanelAskLimiter
{
    public static readonly TimeSpan PairDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RejectionDelay = TimeSpan.FromSeconds(120);
    private readonly Dictionary<(Guid Sender, Guid Target), TimeSpan> _pairs = new();
    private readonly Dictionary<Guid, TimeSpan> _senders = new();
    private readonly Dictionary<Guid, TimeSpan> _targets = new();
    private readonly HashSet<(Guid Sender, Guid Target)> _blocked = new();

    public bool TryRequest(Guid sender, Guid target, TimeSpan now)
    {
        Prune(_pairs, now); Prune(_senders, now); Prune(_targets, now);
        if (_blocked.Contains((sender, target)) || _pairs.ContainsKey((sender, target)) ||
            _senders.ContainsKey(sender) || _targets.ContainsKey(target)) return false;
        _pairs[(sender, target)] = now + PairDelay;
        _senders[sender] = now + TimeSpan.FromSeconds(5);
        _targets[target] = now + TimeSpan.FromSeconds(10);
        return true;
    }

    public void Decline(Guid sender, Guid target, TimeSpan now, bool block = false)
    {
        _pairs[(sender, target)] = now + RejectionDelay;
        if (block) _blocked.Add((sender, target));
    }

    public void Clear() { _pairs.Clear(); _senders.Clear(); _targets.Clear(); _blocked.Clear(); }

    private static void Prune<T>(Dictionary<T, TimeSpan> values, TimeSpan now) where T : notnull
    {
        foreach (var (key, expiry) in values.ToArray())
            if (expiry <= now) values.Remove(key);
    }
}

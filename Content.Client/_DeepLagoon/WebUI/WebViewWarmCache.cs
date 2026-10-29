namespace Content.Client._DeepLagoon.WebUI;

/// <summary>One idle browser per client, measured from its last transition out of use.</summary>
public sealed class WebViewWarmCache
{
    public const int Capacity = 1;
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromSeconds(30);

    private object? _owner;
    private Action? _close;
    public TimeSpan? ExpiresAt { get; private set; }

    public void Retain(object owner, TimeSpan now, Action close)
    {
        // Repeated hide/exit notifications are not actual use and must not renew the lease.
        if (ReferenceEquals(_owner, owner)) return;
        Clear();
        _owner = owner;
        _close = close;
        ExpiresAt = now + IdleLifetime;
    }

    public void Remove(object owner)
    {
        if (!ReferenceEquals(_owner, owner)) return;
        _owner = null;
        _close = null;
        ExpiresAt = null;
    }

    public void Expire(TimeSpan now)
    {
        if (ExpiresAt is { } expiry && now >= expiry) Clear();
    }

    public void Clear()
    {
        var close = _close;
        _owner = null;
        _close = null;
        ExpiresAt = null;
        close?.Invoke();
    }
}

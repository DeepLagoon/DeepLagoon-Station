using Robust.Shared.Configuration;

namespace Content.Server._DeepLagoon.CrashRecovery;

[RegisterComponent]
public sealed partial class RecoveryIdentityComponent : Component
{
    [DataField] public Guid Identity = Guid.NewGuid();
}

/// <summary>Keep implant charges/cooldowns without restoring an action attached to the old body.</summary>
[RegisterComponent]
public sealed partial class RecoveryImplantStateComponent : Component
{
    [DataField] public bool Valid;
    [DataField] public int? Charges;
    [DataField] public int? MaxCharges;
    [DataField] public bool Enabled;
    [DataField] public bool Toggled;
    [DataField] public TimeSpan? CooldownRemaining;
}

[RegisterComponent]
public sealed partial class RecoveryBodyComponent : Component
{
    public Guid User;
    public int Profile;
    public int Slot = -1;
    public bool Registered;
    public HashSet<EntityUid> StartingImplants = new();
}

[RegisterComponent]
public sealed partial class RecoveryShipOwnerComponent : Component
{
    [DataField] public Guid User;
    [DataField] public int Profile;
}

/// <summary>Private apartment gear escrow has an owner even after its body/mind disappears.</summary>
[RegisterComponent]
public sealed partial class RecoveryEscrowComponent : Component
{
    public Guid User;
    public int Profile;
    public Dictionary<EntityUid, string[]> ReturnPaths = new();
}

[CVarDefs]
public sealed class RecoveryCVars
{
    // Explicit opt-in: deploying an unconfigured build must not touch production data.
    public static readonly CVarDef<bool> Enabled = CVarDef.Create("crash_recovery.enabled", false, CVar.SERVERONLY);
    public static readonly CVarDef<string> Scope = CVarDef.Create("crash_recovery.scope", "main", CVar.SERVERONLY);
    public static readonly CVarDef<float> BudgetMilliseconds = CVarDef.Create("crash_recovery.budget_ms", 0.25f, CVar.SERVERONLY);
    public static readonly CVarDef<int> MaxEntitiesPerTick = CVarDef.Create("crash_recovery.entities_per_tick", 4, CVar.SERVERONLY);
    public static readonly CVarDef<int> MaxPending = CVarDef.Create("crash_recovery.max_pending", 4096, CVar.SERVERONLY);
    public static readonly CVarDef<int> CheckpointSeconds = CVarDef.Create("crash_recovery.checkpoint_seconds", 900, CVar.SERVERONLY);
}

/// <summary>Deduplicated FIFO. Repeated container spam cannot grow its allocation or starve other owners.</summary>
public sealed class RecoveryWorkQueue<T> where T : notnull
{
    private readonly Queue<T> _queue = new();
    private readonly HashSet<T> _set = new();
    public int Count => _set.Count;
    public bool Enqueue(T value, int limit)
    {
        if (_set.Contains(value)) return true;
        if (_set.Count >= limit) return false;
        _set.Add(value);
        _queue.Enqueue(value);
        return true;
    }
    public bool TryDequeue(out T value)
    {
        if (!_queue.TryDequeue(out value!)) return false;
        _set.Remove(value);
        return true;
    }
    public void Clear() { _queue.Clear(); _set.Clear(); }
}

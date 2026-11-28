namespace Content.Server._DeepLagoon.Administration;

/// <summary>Preserves cash attribution when its component is removed through admin tools.</summary>
public sealed class AdminCashProvenanceSystem : EntitySystem
{
    private readonly Dictionary<EntityUid, List<AdminCashPortion>> _pending = new();
    private readonly List<EntityUid> _restore = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<AdminCashProvenanceComponent, ComponentRemove>(OnRemove);
    }

    private void OnRemove(EntityUid uid, AdminCashProvenanceComponent component, ComponentRemove args)
    {
        // Spending the last tracked unit legitimately removes an empty component.
        if (TerminatingOrDeleted(uid) || component.Portions.Count == 0)
            return;

        if (!_pending.TryGetValue(uid, out var saved))
            _pending.Add(uid, saved = new List<AdminCashPortion>());

        // Do not retain VV-editable objects belonging to the removed component.
        foreach (var portion in component.Portions)
        {
            saved.Add(new AdminCashPortion
            {
                Spawner = portion.Spawner,
                Name = portion.Name,
                Units = portion.Units,
            });
        }
    }

    /// <summary>
    /// Restores pending attribution before a deposit, split, merge or consumption can use this cash,
    /// including operations in the same tick as component removal.
    /// </summary>
    public AdminCashProvenanceComponent? GetProvenance(EntityUid uid)
    {
        if (TerminatingOrDeleted(uid))
        {
            _pending.Remove(uid);
            return null;
        }

        if (_pending.Remove(uid, out var saved))
            EnsureComp<AdminCashProvenanceComponent>(uid).Portions.InsertRange(0, saved);

        return TryComp<AdminCashProvenanceComponent>(uid, out var component) ? component : null;
    }

    public AdminCashProvenanceComponent EnsureProvenance(EntityUid uid)
        => GetProvenance(uid) ?? EnsureComp<AdminCashProvenanceComponent>(uid);

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_pending.Count == 0)
            return;

        // ComponentRemove runs before the engine finishes detaching the old component.
        // Reattach on the next update, rather than adding a component inside that callback.
        _restore.Clear();
        _restore.AddRange(_pending.Keys);
        foreach (var uid in _restore)
            GetProvenance(uid);
        _restore.Clear();
    }

    public override void Shutdown()
    {
        _pending.Clear();
        _restore.Clear();
        base.Shutdown();
    }
}

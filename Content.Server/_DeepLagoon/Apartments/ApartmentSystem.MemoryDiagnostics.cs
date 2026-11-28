namespace Content.Server._DeepLagoon.Apartments;

public sealed partial class ApartmentSystem
{
    public void CollectMemoryDiagnostics(Action<string, long> add)
    {
        add("apartments.instances", _instances.Count);
        add("apartments.guest_policies", _guestAccess.Count);
        add("apartments.templates", _templates.Count);
        add("apartments.visits", _visits.Count);
        add("apartments.gear_returns", _pendingGearReturns.Count);
        add("apartments.editors", _editors.Count);
        add("apartments.snapshot_gates", _snapshotLocks.Count);
        add("apartments.snapshot_request_keys", _snapshotRequests.Count);
        add("apartments.apply_tasks", _applyTasks.Count);
        add("apartments.external_edits", _externalEdits.Count);
        add("apartments.writer_pending", (_store as ApartmentLayoutStore)?.PendingWrites ?? 0);
    }
}

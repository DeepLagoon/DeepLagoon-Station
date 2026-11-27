namespace Content.Server._DeepLagoon.Currency;

public sealed partial class LagoonCoinSystem
{
    public void CollectMemoryDiagnostics(Action<string, long> add)
    {
        add("lc.participation", _participation.Count);
        add("lc.ready", _ready.Count);
        add("lc.ready_candidates", _readyCandidates.Count);
        add("lc.pending_rewards", _pending.Count);
        add("lc.unpersisted", _unpersisted.Count);
        add("lc.persist_batch", _persistBatch?.Length ?? 0);
        add("lc.requests", _requests.Count);
        add("lc.pending_transfers", _pendingTransfers.Count);
        add("lc.outbox_writer_pending", _outbox?.PendingWrites ?? 0);
    }
}

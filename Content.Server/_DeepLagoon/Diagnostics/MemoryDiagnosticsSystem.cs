#if !DEEPLAGOON_STOCK_ENGINE && DEEPLAGOON_ENGINE_MEMORY_DIAGNOSTICS
using Content.Server._DeepLagoon.Apartments;
using Content.Server._DeepLagoon.Currency;
using Content.Server.GameTicking;
using Content.Server.NPC.Pathfinding;
using Robust.Server.Diagnostics;

namespace Content.Server._DeepLagoon.Diagnostics;

public sealed class MemoryDiagnosticsSystem : EntitySystem
{
    [Dependency] private ServerMemoryDiagnostics _memory = default!;
    [Dependency] private ApartmentSystem _apartments = default!;
    [Dependency] private LagoonCoinSystem _coins = default!;
    [Dependency] private PathfindingSystem _paths = default!;
    [Dependency] private GameTicker _ticker = default!;

    public override void Initialize()
    {
        base.Initialize();
        _memory.CollectCounters += Collect;
    }

    public override void Shutdown()
    {
        _memory.CollectCounters -= Collect;
        base.Shutdown();
    }

    private void Collect(MemoryDiagnosticCounters counters)
    {
        counters.Add("round.id", _ticker.RoundId);
        _apartments.CollectMemoryDiagnostics(counters.Add);
        _coins.CollectMemoryDiagnostics(counters.Add);
        _paths.CollectMemoryDiagnostics(counters.Add);
    }
}
#endif

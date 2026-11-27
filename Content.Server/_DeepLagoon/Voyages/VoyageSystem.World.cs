using Content.Server.GameTicking;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Voyages;
using Content.Shared._NF.Bank.Components;
using Content.Shared.GameTicking;
using Robust.Shared.Asynchronous;
namespace Content.Server._DeepLagoon.Voyages;
public sealed partial class VoyageSystem
{
    [Dependency] private ITaskManager _tasks = default!;
    private VoyageWorld _world = new();
    public VoyageWorld WorldSnapshot => _world;
    public VoyageRouteModel RouteGraph => _galaxy;
    public bool Ready => _ready && _allowWorld && !_world.Closed;
    public Task RefreshGalaxy() => RefreshWorld();
    private VoyageRouteModel _galaxy = new();
    private readonly Guid _run = Guid.NewGuid();
    private readonly Dictionary<Guid, VoyageJob> _refunds = new();
    private bool _allowWorld = true, _worldBusy, _closePending;
    private Guid _closingCycle;
    private TimeSpan _nextClose;
    private int _generation;
    private TimeSpan _nextWorld;
    private void InitializeWorld()
    {
        SubscribeLocalEvent<RoundEndedEvent>(args => { if (!args.Forced) CloseWorld(); else SuspendWorld(); });
        SubscribeLocalEvent<GameRunLevelChangedEvent>(args =>
        {
            if (args.New == GameRunLevel.PostRound)
            {
                if (EntityManager.System<GameTicker>().RoundEndForced) SuspendWorld(); else CloseWorld();
            }
            else if (args.Old == GameRunLevel.InRound && args.New == GameRunLevel.PreRoundLobby) SuspendWorld();
            else if (args.New == GameRunLevel.InRound) { _allowWorld = true; _nextWorld = TimeSpan.Zero; }
        });
    }
    private void SuspendWorld()
    {
        if (!_allowWorld) return;
        // A forced restart keeps the persisted cycle and research payouts intact.
        _allowWorld = false; _ready = false; _generation++;
    }
    private void CloseWorld()
    {
        if (!_allowWorld) return;
        _allowWorld = false; _ready = false; _generation++; _known.Clear();
        _closingCycle = _world.Cycle; _closePending = true; _nextClose = TimeSpan.Zero;
        _world.Closed = true; _world.Known.Clear(); _world.Routes.Clear();
        RetryCloseWorld();
    }
    private void RetryCloseWorld()
    {
        if (_timing.RealTime < _nextClose) return;
        _nextClose = _timing.RealTime + TimeSpan.FromSeconds(5);
        try
        {
            var task = _db.CloseVoyageWorldAsync(_closingCycle); _tasks.BlockWaitOnTask(task); task.GetAwaiter().GetResult();
            _closePending = false;
        }
        catch (Exception e) { Log.Error($"Voyage round closure requires retry: {e.Message}"); }
    }
    private async Task RetryWorld()
    {
        try { await RefreshWorld(); }
        catch (Exception e) { Log.Warning($"Voyage market refresh deferred: {e.Message}"); }
    }
    private async Task RefreshWorld()
    {
        if (_worldBusy) return; _worldBusy = true; var generation = _generation;
        try
        {
            var world = await _db.ReadVoyageWorldAsync(_allowWorld, _galaxy.Lanes.Select(l => VoyageWorld.Key(l.From, l.To)).ToArray(), Now);
            if (generation != _generation) return;
            _world = world; _galaxy.SetGalaxy(world.Seed, world.Cycle); _known.Clear(); _known.UnionWith(world.Known);
            for (var i = 0; i < _galaxy.Systems.Count; i++) if (_known.Contains(_galaxy.Systems[i].Id)) _galaxy.Systems[i] = _galaxy.Systems[i] with { Known = true };
            for (var i = 0; i < _galaxy.Lanes.Count; i++)
                _galaxy.Lanes[i] = _galaxy.Lanes[i] with { Charted = _galaxy.System(_galaxy.Lanes[i].From).Known && _galaxy.System(_galaxy.Lanes[i].To).Known };
        }
        finally { _worldBusy = false; _nextWorld = _timing.RealTime + TimeSpan.FromSeconds(10); }
    }
    private async Task Refund(VoyageJob job)
    {
        try
        {
            var receipt = await _db.ClaimVoyageRefundAsync(job.Id, _run);
            if (receipt.RefundOwner != _run || receipt.Cycle != _world.Cycle || _world.Closed) { _refunds.Remove(job.Id); return; }
            if (_bank.TryVoyageRefund(job.Id, (SectorBankAccount)job.Account, job.Reward)) _refunds.Remove(job.Id);
        }
        catch (Exception e) { Log.Warning($"Voyage refund {job.Id} deferred: {e.Message}"); }
    }
    private double Risk(string[] path, int level, bool patrol)
    {
        var leg = _world.Risk(path, Now, level, patrol); return 1 - (1 - leg) * (1 - leg);
    }
}

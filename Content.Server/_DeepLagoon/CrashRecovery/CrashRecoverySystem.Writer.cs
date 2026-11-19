using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Markdown.Mapping;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.Server._DeepLagoon.CrashRecovery;

public sealed partial class CrashRecoverySystem
{
    private sealed record PendingState(RecoveryAsset Asset, MappingDataNode? Node);
    private readonly Dictionary<Guid, PendingState> _pending = new();
    private readonly Dictionary<Guid, RecoveryPurchase> _purchases = new();
    private readonly Queue<PendingState[]> _atomic = new();
    private Task? _writer;
    private readonly object _writerGate = new();
    private Task? _close;
    private PendingState[] _inFlight = [];
    private RecoveryPurchase[] _inFlightPurchases = [];
    private int _writeFailures;
    private bool _inFlightAtomic;
    private PendingState[]? _retryAtomic;
    public bool CanAcceptCriticalChange => !_enabled || _open && !_closing && _atomic.Count < 1024 && _writeFailures == 0;
    private bool _restartRun;
    private bool _closeFailureReported;
    private bool _stopping;
    private readonly HashSet<Guid> _encodingFailures = new();
    public bool CanProtectBuyer(EntityUid buyer) => !_enabled || CanAcceptCriticalChange && HasComp<RecoveryBodyComponent>(buyer);

    private void Stage(RecoveryAsset asset, MappingDataNode? node = null)
    {
        if (_pending.TryGetValue(asset.Identity, out var previous) && previous.Asset.Revision > asset.Revision) return;
        _pending[asset.Identity] = new(asset, node);
    }

    private void StageAtomic(params RecoveryAsset[] assets)
    {
        foreach (var asset in assets)
            if (_pending.TryGetValue(asset.Identity, out var old) && old.Asset.Revision <= asset.Revision)
                _pending.Remove(asset.Identity);
        _atomic.Enqueue(assets.Select(a => new PendingState(a, null)).ToArray());
    }

    private void PollBootAndWriter()
    {
        if (_restartRun && _close is { IsCompletedSuccessfully: true })
        {
            _restartRun = false;
            _pending.Clear(); _purchases.Clear(); _atomic.Clear();
            _retryAtomic = null; _writeFailures = 0; _retryAt = TimeSpan.Zero;
            _writer = null; _boot = null; _inFlight = []; _inFlightPurchases = [];
            BeginRun();
        }
        if (_close is { IsFaulted: true } && !_closeFailureReported)
        {
            Log.Error($"Recovery clean-close failed: {_close.Exception?.GetBaseException().Message}");
            _closeFailureReported = true;
        }
        if (_boot is { IsCompleted: true } boot)
        {
            _boot = null;
            if (boot.IsCompletedSuccessfully)
            {
                _open = true;
                QueueWarmup();
                LoadIncidents(boot.GetAwaiter().GetResult());
            }
            else
            {
                Log.Warning($"Recovery startup waiting for database/lease: {boot.Exception?.GetBaseException().Message}");
                _retryAt = _timing.RealTime + TimeSpan.FromSeconds(5);
            }
        }
        if (!_open && !_closing && _boot == null && _timing.RealTime >= _retryAt && (_close == null || _close.IsCompletedSuccessfully))
            _boot = _db.BootCrashRecovery(_scope, _run);
        if (_writer is not { IsCompleted: true } writer) return;
        _writer = null;
        if (writer.IsCompletedSuccessfully)
        {
            _writeFailures = 0;
        }
        else
        {
            _writeFailures++;
            if (_inFlightAtomic) _retryAtomic = _inFlight;
            else foreach (var state in _inFlight)
                    if (!_pending.TryGetValue(state.Asset.Identity, out var newer) || newer.Asset.Revision < state.Asset.Revision)
                        _pending[state.Asset.Identity] = state;
            foreach (var receipt in _inFlightPurchases) _purchases.TryAdd(receipt.Identity, receipt);
            _retryAt = _timing.RealTime + TimeSpan.FromSeconds(Math.Min(60, 1 << Math.Min(_writeFailures, 6)));
            Log.Warning($"Recovery write deferred; previous data retained; attempt={_writeFailures}: {writer.Exception?.GetBaseException().Message}");
        }
        _inFlight = []; _inFlightPurchases = [];
    }

    private void StartWriter(TimeSpan now)
    {
        lock (_writerGate) StartWriterLocked(now);
    }

    private void StartWriterLocked(TimeSpan now)
    {
        if (_signalClosing || _writer != null || now < _retryAt || _atomic.Count == 0 && _pending.Count == 0 && _purchases.Count == 0 && now < _nextHeartbeat) return;
        _nextHeartbeat = now + TimeSpan.FromSeconds(10);
        _inFlightAtomic = _retryAtomic != null || _atomic.Count > 0;
        _inFlight = _retryAtomic ?? (_atomic.TryDequeue(out var atomic) ? atomic : _pending.Values.Take(64).ToArray());
        _retryAtomic = null;
        _inFlightPurchases = _purchases.Values.Take(64).ToArray();
        foreach (var entry in _inFlight)
            if (_pending.TryGetValue(entry.Asset.Identity, out var pending) && pending.Asset.Revision <= entry.Asset.Revision)
                _pending.Remove(entry.Asset.Identity);
        foreach (var receipt in _inFlightPurchases) _purchases.Remove(receipt.Identity);
        var batch = _inFlight;
        var purchases = _inFlightPurchases;
        var run = _run;
        var scope = _scope;
        // Only detached serialization trees cross this boundary. No components, entities or prototype caches.
        async Task WriteBatch()
        {
            var assets = new RecoveryAsset[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                var state = batch[i];
                if (state.Node == null) { assets[i] = state.Asset; continue; }
                try
                {
                    using var output = new StringWriter();
                    new YamlStream(new YamlDocument(state.Node.ToYaml())).Save(new YamlMappingFix(new Emitter(output)), false);
                    var data = output.ToString();
                    if (data.Length > 1024 * 1024) throw new InvalidDataException("Entity exceeds the 1 MiB snapshot limit.");
                    assets[i] = state.Asset with { Data = data };
                    _encodingFailures.Remove(state.Asset.Identity);
                }
                catch (Exception error)
                {
                    assets[i] = state.Asset; // retain this entity's previous good data; never block other owners
                    if (_encodingFailures.Add(state.Asset.Identity))
                        Log.Error($"Recovery entity encoding deferred: {state.Asset.Identity}: {error.GetType().Name}");
                }
            }
            await _db.WriteCrashRecovery(scope, run, assets, purchases);
        }
        // The engine's deterministic in-memory test database explicitly forbids cross-thread
        // access. Production uses its asynchronous backend; the test-only mode stays deterministic.
        _writer = _synchronousDatabase ? WriteBatch() : Task.Run(WriteBatch);
    }

    /// <summary>Only explicit normal lifecycle paths call this; EntitySystem.Shutdown may follow a fatal error.</summary>
    public Task CloseNormally()
    {
        if (_close is { IsFaulted: true }) _closing = false;
        if (!_enabled || _closing) return _close ?? Task.CompletedTask;
        _closeFailureReported = false;
        _closing = true;
        _open = false;
        _pending.Clear(); _purchases.Clear(); _atomic.Clear();
        var priorWriter = _writer;
        var priorBoot = _boot;
        var priorClaims = _bundles.Where(bundle => bundle.Claim != null).Select(bundle => (Task)bundle.Claim!).ToArray();
        var scope = _scope;
        var run = _run;
        async Task CloseRun()
        {
            // A failed snapshot must not turn an explicitly completed round into a crash.
            if (priorWriter != null) try { await priorWriter; } catch { /* closing invalidates this run */ }
            if (priorBoot != null) try { await priorBoot; } catch { /* no open run may have been created */ }
            foreach (var claim in priorClaims)
                try { await claim; } catch { /* failed claims retain their source entitlement */ }
            await _db.CloseCrashRecovery(scope, run);
        }
        _close = _synchronousDatabase ? CloseRun() : Task.Run(CloseRun);
        return _close;
    }

    public async Task StopWorker()
    {
        _stopping = true;
        _signalTarget = null;
        var tasks = new List<Task>();
        if (_writer != null) tasks.Add(_writer);
        if (_boot != null) tasks.Add(_boot);
        if (_close != null) tasks.Add(_close);
        tasks.AddRange(_profiles.Values);
        tasks.AddRange(_bundles.Where(b => b.Claim != null).Select(b => (Task)b.Claim!));
        foreach (var task in tasks)
            try { await task.ConfigureAwait(false); }
            catch { /* failure was reported by its normal polling path; no clean marker is written here */ }
    }
}

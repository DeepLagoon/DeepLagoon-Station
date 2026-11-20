using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Asynchronous;
using System.Linq;
using Content.Server.Spreader;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Map.Events;
using Robust.Shared.Player;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._DeepLagoon.Apartments;

public sealed partial class ApartmentSystem
{
    [Dependency] private IDependencyCollection _snapshotDependencies = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SpreaderSystem _spreader = default!;
    [Dependency] private ITaskManager _persistenceTasks = default!;
    private readonly Dictionary<Instance, SemaphoreSlim> _snapshotLocks = new();
    private readonly Dictionary<Instance, int> _snapshotRequests = new();
    private readonly List<Instance> _unloading = new();
    private Task _maintenanceSave = Task.CompletedTask;
    private readonly Dictionary<Robust.Shared.Network.NetUserId, Task<string?>> _applyTasks = new();
    private bool _closingPersistence;
    private readonly Dictionary<Instance, (int Count, TaskCompletionSource Done)> _externalEdits = new();
    private sealed class EditLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    public IDisposable? BeginDecorationChange(EntityUid target)
    {
        if (_closingPersistence || !TryComp<TransformComponent>(target, out var transform)) return null;
        var instance = _instances.Values.FirstOrDefault(home => home.Grid == target || home.Grid == transform.GridUid);
        if (instance == null) return null;
        if (!_externalEdits.TryGetValue(instance, out var pending)) pending = (0, new(TaskCreationOptions.RunContinuationsAsynchronously));
        _externalEdits[instance] = (pending.Count + 1, pending.Done);
        return new EditLease(() =>
        {
            var current = _externalEdits[instance];
            if (current.Count > 1) _externalEdits[instance] = (current.Count - 1, current.Done);
            else { _externalEdits.Remove(instance); current.Done.SetResult(); }
        });
    }

    private void AwaitApartmentOperations()
    {
        _persistenceTasks.BlockWaitOnTask(Task.WhenAll(_externalEdits.Values.Select(p => p.Done.Task).ToArray()));
        _persistenceTasks.BlockWaitOnTask(Task.WhenAll(_shopWrites.Values.Select(p => p.Task).ToArray()));
        _persistenceTasks.BlockWaitOnTask(Task.WhenAll(_applyTasks.Values.ToArray()));
        _persistenceTasks.BlockWaitOnTask(_maintenanceSave);
    }
    private T WaitPersistence<T>(Task<T> task) { _persistenceTasks.BlockWaitOnTask(task); return task.GetAwaiter().GetResult(); }
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SaveRetryDelay = TimeSpan.FromMinutes(1);

    private readonly record struct SnapshotSerializationTiming(
        long EntityWalkDone, long SnapshotBuildDone, long RoundEndCaptureDone);

    public enum ApartmentSaveReason
    {
        Manual,
        Checkpoint,
        Unload,
        RoundCleanup,
        Shutdown,
        Layout,
        Decoration,
        Delivery,
    }

    private IApartmentLayoutStore OpenStore()
    {
        var store = new ApartmentLayoutStore(_resources.UserData.RootDir is { } root
            ? Path.Combine(root, "apartments-prototype.db") : ":memory:");
        // An interrupted server also ends its round. Finalize trash in unloaded apartments.
        store.FinishRound();
        return store;
    }

    private IEnumerable<EntityUid> ApartmentEntities(EntityUid parent)
    {
        var children = Transform(parent).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            yield return child;
            foreach (var descendant in ApartmentEntities(child)) yield return descendant;
        }
    }

    private IEnumerable<EntityUid> TrashContents(Instance instance)
    {
        // Empty apartments are paused; normal entity queries intentionally skip them.
        foreach (var entity in ApartmentEntities(instance.Grid))
        {
            if (!HasComp<ApartmentTrashComponent>(entity) || !TryComp<ContainerManagerComponent>(entity, out var manager)) continue;
            foreach (var container in manager.Containers.Values)
            foreach (var item in container.ContainedEntities)
                yield return item;
        }
    }

    private (ApartmentSnapshotCapture Capture, int EntityCount, SnapshotSerializationTiming Timing)
        CaptureApartment(Instance instance, ref string stage, out SnapshotCaptureDiagnostics? diagnostics)
    {
        diagnostics = _config.GetCVar(Robust.Shared.CVars.PerformanceLogLevel) >= 2 ? new() : null;
        using var captureDiagnostics = diagnostics;
        diagnostics?.Stage("exclusions");
        stage = "entity_walk";
        var excluded = new HashSet<EntityUid>();
        void Exclude(EntityUid uid)
        {
            if (!Exists(uid) || !excluded.Add(uid)) return;
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child)) Exclude(child);
        }
        foreach (var visit in _visits.Values)
        {
            Exclude(visit.Body);
            foreach (var gear in visit.Gear) Exclude(gear.Item);
        }
        var minds = EntityQueryEnumerator<MindComponent>();
        while (minds.MoveNext(out var uid, out _)) Exclude(uid);
        var actors = EntityQueryEnumerator<ActorComponent>();
        while (actors.MoveNext(out var uid, out _)) Exclude(uid);
        var exclusionByEntity = new Dictionary<EntityUid, bool>();
        bool Excluded(EntityUid uid)
        {
            if (uid == instance.Grid)
                return false;
            if (exclusionByEntity.TryGetValue(uid, out var cached))
                return cached;

            var result = !uid.IsValid() || excluded.Contains(uid) || HasComp<ApartmentChameleonGearComponent>(uid) ||
                HasComp<ActorComponent>(uid) || TryComp<MindContainerComponent>(uid, out var mind) && mind.Mind != null ||
                !TryComp<TransformComponent>(uid, out var transform) ||
                (transform.ParentUid.IsValid() ? Excluded(transform.ParentUid) : HasComp<MapComponent>(uid) || HasComp<MapGridComponent>(uid));
            exclusionByEntity.Add(uid, result);
            return result;
        }
        HashSet<EntityUid> roots = [instance.Grid];
        diagnostics?.Stage("before_hooks");
        RaiseLocalEvent(new BeforeSerializationEvent(roots, [Transform(instance.Grid).MapID], FileCategory.Grid));
        diagnostics?.Stage("serializer_ctor");
        var serializer = new EntitySerializer(_snapshotDependencies, new SerializationOptions
        {
            Category = FileCategory.Grid, ExpectPreInit = false, MissingEntityBehaviour = MissingEntityBehaviour.IncludeNullspace,
        });
        serializer.ErroringEntities.UnionWith(excluded);
        serializer.OnIsSerializeable += (Entity<MetaDataComponent> entity, ref bool serializable) => serializable &= !Excluded(entity.Owner);
        diagnostics?.Stage("identity");
        foreach (var (id, entity) in instance.Furniture)
            if (Exists(entity)) EnsureComp<ApartmentFurnitureIdentityComponent>(entity).Id = id;
        diagnostics?.Stage("recursive");
        if (roots.Count == 1 && roots.Contains(instance.Grid))
            serializer.SerializeEntityRecursive(instance.Grid);
        else
            serializer.SerializeEntityRecursive(roots);

        var entityWalkDone = Stopwatch.GetTimestamp();
        stage = "snapshot_build";
        diagnostics?.Stage("snapshot_build");
        var data = serializer.Write();
        foreach (var entity in serializer.EntityData.Values)
        {
            if (!entity.Node.TryGet<SequenceDataNode>("components", out var components)) continue;
            foreach (var component in components.Cast<MappingDataNode>())
            {
                var type = component.Get<ValueDataNode>("type").Value;
                if (type == "Strap" && component.TryGet<SequenceDataNode>("buckledEntities", out var buckled))
                    foreach (var entry in buckled.Cast<ValueDataNode>().Where(entry => entry.Value == "invalid").ToArray()) buckled.Remove(entry);
                if (type != "ContainerContainer" || !component.TryGet<MappingDataNode>("containers", out var containers)) continue;
                foreach (var container in containers.Values.Cast<MappingDataNode>())
                    if (container.TryGet<SequenceDataNode>("ents", out var contents))
                        foreach (var entry in contents.Cast<ValueDataNode>().Where(entry => entry.Value == "invalid").ToArray()) contents.Remove(entry);
            }
        }
        diagnostics?.Stage("after_hooks");
        RaiseLocalEvent(new AfterSerializationEvent(roots, data, FileCategory.Grid));

        var snapshotBuildDone = Stopwatch.GetTimestamp();
        stage = "round_end_capture";
        diagnostics?.Stage("round_end");
        var trash = new HashSet<EntityUid>();
        void CollectTrash(EntityUid uid)
        {
            if (!Exists(uid) || !trash.Add(uid)) return;
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child)) CollectTrash(child);
        }
        foreach (var item in TrashContents(instance)) CollectTrash(item);
        if (trash.Count == 0)
            return (new ApartmentSnapshotCapture(data, Array.Empty<string>(), Array.Empty<string>()), serializer.EntityData.Count,
                new(entityWalkDone, snapshotBuildDone, Stopwatch.GetTimestamp()));

        var removedIds = trash.Where(serializer.YamlUidMap.ContainsKey).Select(uid => serializer.YamlUidMap[uid].ToString(System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        var trashBinIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bin in ApartmentEntities(instance.Grid))
            if (HasComp<ApartmentTrashComponent>(bin) && serializer.YamlUidMap.TryGetValue(bin, out var id))
                trashBinIds.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // All map IDs/time offsets and serialization hooks have been resolved on this thread.
        // Transfer only the freshly built node graph and materialized YAML IDs to the worker;
        // never capture the serializer, this system, Instance, or a deferred ECS enumeration.
        return (new ApartmentSnapshotCapture(data, removedIds, trashBinIds), serializer.EntityData.Count,
            new(entityWalkDone, snapshotBuildDone, Stopwatch.GetTimestamp()));
    }

    public bool SaveApartment(Instance instance, ApartmentSaveReason reason = ApartmentSaveReason.Manual, Guid delivery = default)
        => WaitPersistence(SaveApartmentAsync(instance, reason, delivery));

    public async Task<bool> SaveApartmentAsync(Instance instance, ApartmentSaveReason reason = ApartmentSaveReason.Manual, Guid delivery = default)
    {
        if (!_snapshotLocks.TryGetValue(instance, out var gate)) _snapshotLocks.Add(instance, gate = new SemaphoreSlim(1, 1));
        _snapshotRequests[instance] = _snapshotRequests.GetValueOrDefault(instance) + 1;
        await gate.WaitAsync();
        try { return await SaveApartmentCoreAsync(instance, reason, delivery); }
        finally { gate.Release(); _snapshotRequests[instance]--; }
    }

    private async Task<bool> SaveApartmentCoreAsync(Instance instance, ApartmentSaveReason reason, Guid delivery)
    {
        // The round reset and shutdown destroy the grids even if a snapshot fails.
        // Give each grid one final opportunity to persist before it is destroyed.
        var finalSave = reason is ApartmentSaveReason.RoundCleanup or ApartmentSaveReason.Shutdown;
        if ((!finalSave && _timing.RealTime < instance.RetrySaveAt) || !Exists(instance.Grid) ||
            reason == ApartmentSaveReason.Checkpoint && instance.Frozen)
            return false;

        var started = Stopwatch.GetTimestamp();
        var syncStarted = started;
        var prepareMain = TimeSpan.Zero;
        var prepareWait = TimeSpan.Zero;
        WriterPreparationResult preparation = default;
        var measurePreparation = _config.GetCVar(Robust.Shared.CVars.PerformanceLogLevel) >= 2;
        long prepareThreadBytes = 0;
        var prepareGcPause = TimeSpan.Zero;
        long layoutDone;
        long serializationDone;
        long storageQueuedAt;
        SnapshotSerializationTiming timing;
        ApartmentSnapshotWriteResult writeTiming;
        SnapshotCaptureDiagnostics? captureDiagnostics = null;
        var stage = "layout";
        int? entityCount = null;
        int? yamlChars = null;
        try
        {
            stage = "writer_preparation";
            var originalGrid = instance.Grid;
            var tracked = _instances.GetValueOrDefault(instance.Owner) == instance || _retiring.Contains(instance);
            long preparedAt = 0;
            try
            {
                // Collection/metadata planning can fail synchronously as well as Compile
                // asynchronously. Both are optional optimizations of the native writer.
                var allocatedBefore = measurePreparation ? GC.GetAllocatedBytesForCurrentThread() : 0;
                var pauseBefore = measurePreparation ? GC.GetTotalPauseDuration() : TimeSpan.Zero;
                Task<WriterPreparationResult> preparationTask;
                try
                {
                    preparationTask = PrepareApartmentWritersAsync(instance);
                }
                finally
                {
                    // Stop these counters before await. Other ticks can run while the
                    // compiler works, so that interval is not this call's allocation cost.
                    if (measurePreparation)
                    {
                        prepareThreadBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                        prepareGcPause = GC.GetTotalPauseDuration() - pauseBefore;
                    }
                }
                preparedAt = Stopwatch.GetTimestamp();
                prepareMain = Stopwatch.GetElapsedTime(started, preparedAt);
                preparation = await preparationTask;
            }
            catch (Exception error)
            {
                // Preparation is an optimization. Preserve the native cold writer fallback,
                // its validation and durable save semantics if compilation was unavailable.
                if (_timing.RealTime >= _nextWriterPreparationWarning)
                {
                    _nextWriterPreparationWarning = _timing.RealTime + TimeSpan.FromMinutes(1);
                    Log.Warning("Apartment writer preparation failed; using native serialization: {Error}", error.Message);
                }
            }
            syncStarted = Stopwatch.GetTimestamp();
            if (preparedAt == 0)
                prepareMain = Stopwatch.GetElapsedTime(started, syncStarted);
            else
                prepareWait = Stopwatch.GetElapsedTime(preparedAt, syncStarted);
            if (!Exists(originalGrid) || instance.Grid != originalGrid ||
                tracked && _instances.GetValueOrDefault(instance.Owner) != instance && !_retiring.Contains(instance) ||
                reason == ApartmentSaveReason.Checkpoint && instance.Frozen)
                return false;
            stage = "layout";
            // Capture fresh layout/editor/ECS state only AFTER preparation completes.
            var layout = ApartmentLayout.Diff(instance.Template, CurrentLayout(instance), instance.Delta.Revision, MapBaseline(instance));
            layout.OxygenPercent = instance.Delta.OxygenPercent;

            layoutDone = Stopwatch.GetTimestamp();
            var (capture, count, serializationTiming) = CaptureApartment(instance, ref stage, out captureDiagnostics);
            timing = serializationTiming;
            entityCount = count;

            stage = "format_and_storage_worker";
            serializationDone = Stopwatch.GetTimestamp();
            var metadata = new ApartmentSnapshot { Layout = layout, MapFurniture = instance.MapFurniture.Values.ToList() };
            // The store copies mutable metadata synchronously before enqueueing. No ECS or
            // editor state is read after this await to complete the captured snapshot.
            var receipt = Store.SaveCapturedSnapshotAsync(instance.Owner.UserId, instance.Template.ID, capture, metadata, delivery);
            storageQueuedAt = Stopwatch.GetTimestamp();
            writeTiming = await receipt;
            yamlChars = writeTiming.YamlChars;
        }
        catch (Exception exception)
        {
            if (exception.Data["ApartmentSaveStage"] is string workerStage) stage = workerStage;
            var firstFailure = instance.RetrySaveAt == TimeSpan.Zero;
            instance.RetrySaveAt = _timing.RealTime + SaveRetryDelay;
            Log.Log(LogLevel.Error, firstFailure ? exception : null,
                "Apartment snapshot failed, previous save retained: reason={Reason} template={Template} grid={Grid} frozen={Frozen} stage={Stage} elapsed_ms={ElapsedMs:F1} entities={Entities} yaml_chars={YamlChars} retry_delay_s={RetrySeconds} error={ErrorType} detail={ErrorMessage}",
                reason, instance.Template.ID, instance.Grid, instance.Frozen, stage, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                entityCount, yamlChars, SaveRetryDelay.TotalSeconds, exception.GetType().Name, exception.Message);
            return false;
        }

        var completed = Stopwatch.GetTimestamp();
        var recovered = instance.RetrySaveAt != TimeSpan.Zero;
        instance.RetrySaveAt = TimeSpan.Zero;
        instance.NextCheckpoint = _timing.RealTime + CheckpointInterval;
        var elapsed = Stopwatch.GetElapsedTime(started, completed);
        var mainThread = prepareMain + Stopwatch.GetElapsedTime(syncStarted, storageQueuedAt);
        var storageWait = Stopwatch.GetElapsedTime(storageQueuedAt, completed);
        // Awaiting the worker also includes scheduling the continuation on a later game tick.
        // It is save latency, not time for which this operation blocked simulation.
        var level = mainThread >= _timing.TickPeriod || storageWait >= TimeSpan.FromSeconds(1)
            ? LogLevel.Warning : recovered ? LogLevel.Info : LogLevel.Verbose;
        if (Log.IsLogLevelEnabled(level))
        {
            Log.Log(level,
                "Apartment snapshot saved: reason={Reason} template={Template} grid={Grid} frozen={Frozen} recovered={Recovered} elapsed_ms={ElapsedMs:F1} main_thread_ms={MainThreadMs:F1} storage_enqueue_ms={StorageEnqueueMs:F1} storage_await_ms={StorageAwaitMs:F1} layout_ms={LayoutMs:F1} serialization_ms={SerializationMs:F1} entity_walk_ms={EntityWalkMs:F1} snapshot_build_ms={SnapshotBuildMs:F1} round_end_capture_ms={RoundEndCaptureMs:F1} format_queue_wait_ms={FormatQueueWaitMs:F1} yaml_ms={YamlMs:F1} round_end_build_ms={RoundEndBuildMs:F1} round_end_yaml_ms={RoundEndYamlMs:F1} storage_ms={StorageMs:F1} storage_async=True yaml_async=True tick_budget_ms={TickBudgetMs:F1} entities={Entities} yaml_chars={YamlChars}",
                reason, instance.Template.ID, instance.Grid, instance.Frozen, recovered, elapsed.TotalMilliseconds,
                mainThread.TotalMilliseconds, Stopwatch.GetElapsedTime(serializationDone, storageQueuedAt).TotalMilliseconds, storageWait.TotalMilliseconds,
                Stopwatch.GetElapsedTime(syncStarted, layoutDone).TotalMilliseconds,
                Stopwatch.GetElapsedTime(layoutDone, serializationDone).TotalMilliseconds,
                Stopwatch.GetElapsedTime(layoutDone, timing.EntityWalkDone).TotalMilliseconds,
                Stopwatch.GetElapsedTime(timing.EntityWalkDone, timing.SnapshotBuildDone).TotalMilliseconds,
                Stopwatch.GetElapsedTime(timing.SnapshotBuildDone, timing.RoundEndCaptureDone).TotalMilliseconds,
                Math.Max(0, Stopwatch.GetElapsedTime(storageQueuedAt, writeTiming.WorkerStarted).TotalMilliseconds),
                writeTiming.Yaml.TotalMilliseconds, writeTiming.RoundEndBuild.TotalMilliseconds,
                writeTiming.RoundEndYaml.TotalMilliseconds, writeTiming.Storage.TotalMilliseconds,
                _timing.TickPeriod.TotalMilliseconds, entityCount, yamlChars);
        }
        if (preparation.ExpressionsCompiled > 0 && Log.IsLogLevelEnabled(LogLevel.Info))
            Log.Info("Apartment writers prepared: grid={Grid} requested_types={Types} prepared_types={Prepared} deferred_metadata={Deferred} expressions={Expressions} prepare_main_ms={MainMs:F3} prepare_plan_ms={PlanMs:F3} prepare_queue_ms={QueueMs:F3} prepare_compile_ms={CompileMs:F3} prepare_await_ms={AwaitMs:F3} compile_worker_thread={WorkerThread} compile_worker_context={WorkerContext} prepare_metadata_visited={MetadataVisited} prepare_thread_bytes={ThreadBytes} prepare_gc_pause_ms={PauseMs:F3} prepare_counters_enabled={CountersEnabled}",
                instance.Grid, preparation.RequestedTypes, preparation.PreparedTypes, preparation.DeferredTypes,
                preparation.ExpressionsCompiled, prepareMain.TotalMilliseconds, preparation.PlanningMilliseconds,
                preparation.QueueMilliseconds, preparation.CompileMilliseconds, prepareWait.TotalMilliseconds,
                preparation.WorkerThreadId, preparation.WorkerHadSynchronizationContext,
                preparation.MetadataTypesVisited, prepareThreadBytes, prepareGcPause.TotalMilliseconds, measurePreparation);
        if (captureDiagnostics != null && mainThread >= _timing.TickPeriod && Log.IsLogLevelEnabled(LogLevel.Warning))
            Log.Warning("Slow apartment ECS capture: grid={Grid} entities={Entities} nested_stages=False allocations=current_thread prepare_main_ms={PrepareMainMs:F3} prepare_metadata_visited={MetadataVisited} prepare_thread_bytes={ThreadBytes} prepare_gc_pause_ms={PauseMs:F3} {Stages}",
                instance.Grid, entityCount, prepareMain.TotalMilliseconds, preparation.MetadataTypesVisited,
                prepareThreadBytes, prepareGcPause.TotalMilliseconds, captureDiagnostics.Describe());
        return true;
    }

    private Instance RestoreApartment(Robust.Shared.Network.NetUserId owner, ApartmentTemplatePrototype template, ApartmentDelta? selected, ApartmentSnapshot snapshot)
    {
        using var reader = new StringReader(snapshot.Grid);
        if (!_mapLoader.TryLoadGrid(Comp<MapComponent>(_activeMap).MapId, reader, $"apartment {owner}/{template.ID}", out var grid,
                new DeserializationOptions { StoreYamlUids = true }))
            throw new InvalidDataException("Cannot restore apartment snapshot.");
        try
        {
            var layout = snapshot.Layout;
            if (selected != null)
            {
                layout.Revision = selected.Revision;
                layout.OxygenPercent = selected.OxygenPercent;
            }
            EnsureComp<ApartmentAtmosphereSettingsComponent>(grid.Value.Owner).OxygenPercent = Math.Clamp(layout.OxygenPercent, 0, 100);
            var position = FindFreePosition(template);
            _transform.SetCoordinates(grid.Value.Owner, new EntityCoordinates(_activeMap, position));
            // A saved grid starts before its children, and restored spreaders have already passed MapInit.
            _spreader.InitSpreaderGrid(grid.Value.Owner);
            if (template.ID == BitrunApartment)
                RestoreBitrunEnvironment(grid.Value.Owner);
            var instance = new Instance { Owner = owner, Template = template, Grid = grid.Value.Owner, Position = position, Delta = layout,
                Guests = _guestAccess.GetValueOrDefault(owner) ?? new ApartmentGuestAccess() };
            foreach (var mapped in snapshot.MapFurniture) instance.MapFurniture.Add(mapped.Baseline.Id, mapped);
            foreach (var entity in ApartmentEntities(instance.Grid))
                if (TryComp<ApartmentFurnitureIdentityComponent>(entity, out var identity)) instance.Furniture.Add(identity.Id, entity);
            RegisterMapFurniture(instance);
            return instance;
        }
        catch { Del(grid.Value.Owner); throw; }
    }

    private void SaveApartments(ApartmentSaveReason reason)
    {
        // The synchronous lifecycle boundary pumps completions before grids or stores are destroyed.
        AwaitApartmentOperations();
        foreach (var instance in _instances.Values.Concat(_retiring).Distinct().ToArray())
            SaveApartment(instance, reason);
        if (_store != null) _persistenceTasks.BlockWaitOnTask(_store.FlushAsync());
    }

    private async Task MaintainApartment(Instance instance, bool unload)
    {
        try
        {
            if (!await SaveApartmentAsync(instance, unload ? ApartmentSaveReason.Unload : ApartmentSaveReason.Checkpoint)) return;
            if (!unload || !instance.Frozen || _visits.Values.Any(v => v.Apartment == instance) || !Exists(instance.Grid)) return;
            if (_snapshotRequests.GetValueOrDefault(instance) != 0 ||
                _externalEdits.ContainsKey(instance) ||
                _shopWrites.ContainsKey(instance.Owner) ||
                _applyTasks.TryGetValue(instance.Owner, out var editorWrite) && !editorWrite.IsCompleted) return;
            EntityManager.System<Content.Server._DeepLagoon.Performance.StagedMapDeletionSystem>().Queue(instance.Grid);
            if (_instances.GetValueOrDefault(instance.Owner) == instance) _instances.Remove(instance.Owner);
            _retiring.Remove(instance);
            _unloading.Add(instance);
        }
        catch (Exception error) { Log.Error($"Apartment maintenance save failed; grid retained: {error}"); }
    }

    private void FinishApartmentRound()
    {
        AwaitApartmentOperations();
        foreach (var instance in _instances.Values.Concat(_retiring).ToArray())
        foreach (var item in TrashContents(instance).ToArray())
            if (Exists(item)) Del(item);
        if (_store != null) _persistenceTasks.BlockWaitOnTask(_store.FinishRoundAsync());
    }

    private void SpillFurnitureContents(EntityUid entity, EntityUid grid)
    {
        if (!TryComp<ContainerManagerComponent>(entity, out var manager)) return;
        var position = Transform(entity).LocalPosition;
        foreach (var container in manager.Containers.Values)
        foreach (var item in container.ContainedEntities.ToArray())
        {
            _containers.Remove(item, container, force: true);
            _transform.SetCoordinates(item, new EntityCoordinates(grid, position));
        }
    }
}

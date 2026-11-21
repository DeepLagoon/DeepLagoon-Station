using Stopwatch = System.Diagnostics.Stopwatch;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server.Preferences.Managers;
using Content.Server._DeepLagoon.Apartments;
using Content.Server._DeepLagoon.Bitrunning;
using Content.Shared.GameTicking;
using Content.Shared.Item;
using Content.Shared.Body.Part;
using Content.Shared.Body.Organ;
using Content.Shared.Implants.Components;
using Content.Shared.Mind.Components;
using Content.Shared.Preferences;
using Content.Shared.Ghost;
using Robust.Shared.Audio.Components;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.CrashRecovery;

/// <summary>
/// Main-thread work is incremental and globally budgeted. No disk IO, DB waits, whole-world
/// queries, recursive entity serialization or per-container-event snapshots occur during play.
/// </summary>
public sealed partial class CrashRecoverySystem : EntitySystem
{
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IDependencyCollection _dependencies = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private GameTicker _ticker = default!;

    private bool _enabled;
    private bool _synchronousDatabase;
    private bool _open;
    private bool _closing;
    private bool _suppressDeletion;
    private string _scope = "";
    private Guid _run;
    private int _nextYaml;
    private long _revision;
    private EntitySerializer? _serializer;
    private Task<RecoveryBoot>? _boot;
    private TimeSpan _retryAt;
    private readonly Dictionary<EntityUid, Task<int?>> _profiles = new();
    private readonly Dictionary<EntityUid, Guid> _profileUsers = new();
    private readonly Dictionary<EntityUid, int> _profileSlots = new();
    private readonly Dictionary<EntityUid, TimeSpan> _profileRetryAt = new();
    private readonly Dictionary<EntityUid, (Guid User, int Profile, int Slot)> _mindProfiles = new();
    private readonly Dictionary<EntityUid, RecoveryAsset> _assets = new();
    private readonly Dictionary<Guid, EntityUid> _entitiesByIdentity = new();
    private readonly List<EntityUid> _tracked = new();
    private readonly List<EntityUid> _bodies = new();
    private readonly List<EntityUid> _escrowRoots = new();
    private readonly Dictionary<EntityUid, List<EntityUid>> _deferredEscrows = new();
    private readonly RecoveryWorkQueue<EntityUid> _dirty = new();
    private readonly RecoveryWorkQueue<EntityUid> _discover = new();
    private sealed class ChildScan(EntityUid parent, TransformChildrenEnumerator children)
    {
        public EntityUid Parent = parent;
        public TransformChildrenEnumerator Children = children;
        public EntityUid? PendingChild;
        public EntityUid[]? StableChildren;
        public int ChildIndex;
    }
    private readonly Queue<ChildScan> _childScans = new();
    private readonly Queue<ChildScan> _equipmentScans = new();
    private readonly HashSet<EntityUid> _scanningChildren = new();
    private int _workCursor;
    private readonly HashSet<EntityUid> _discoveredEntities = new();
    private readonly HashSet<EntityUid> _childrenDiscovered = new();
    private int _pollCursor;
    private int _bodyCursor;
    private TimeSpan _nextBodyScan;
    private TimeSpan _nextHeartbeat;
    private TimeSpan _nextMetrics;
    private readonly Dictionary<EntityUid, GameTick> _capturedTicks = new();
    private readonly Dictionary<EntityUid, TimeSpan> _failedUntil = new();
    public double LastTickMilliseconds { get; private set; }
    public double MaxTickMilliseconds { get; private set; }
    public double MaxCaptureMilliseconds { get; private set; }
    public string MaxCapturePrototype { get; private set; } = "";
    public int MaxCaptureCollections { get; private set; }
    public int PendingCount => _pending.Count + _dirty.Count + _discover.Count + _childScans.Count + _equipmentScans.Count + _atomic.Count;
    public int TrackedCount => _assets.Count;
    public int CapturedCount => _capturedTicks.Count;
    public bool Ready => !_enabled || _open && _restore.Count == 0 && _warmed;
    public Guid RunIdentity => _run;

    public override void Initialize()
    {
        _scope = _cfg.GetCVar(RecoveryCVars.Scope);
        _synchronousDatabase = _cfg.GetCVar(Content.Shared.CCVar.CCVars.DatabaseSynchronous);
        CrashRecoveryOperations.Prefix(_scope);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnSpawn);
        EntityManager.EntityInitialized += OnEntityInitialized;
        SubscribeLocalEvent<EntParentChangedMessage>(OnParentChanged);
        SubscribeLocalEvent<RecoveryIdentityComponent, EntityTerminatingEvent>(OnItemTerminating);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => EndRound());
        SubscribeLocalEvent<GameRunLevelChangedEvent>(args =>
        {
            if (args.New == GameRunLevel.PostRound || args.New == GameRunLevel.PreRoundLobby && args.Old == GameRunLevel.InRound)
                _ = CloseNormally();
        });
        InitializeLifecycle();
        InitializeRecoveryReader();
        _cfg.OnValueChanged(RecoveryCVars.Enabled, ConfigureEnabled, true);
    }

    private void ConfigureEnabled(bool enabled)
    {
        if (enabled == _enabled) return;
        if (_ticker.RunLevel == GameRunLevel.InRound || _open)
            throw new InvalidOperationException("Change crash recovery configuration in the lobby before the round starts.");
        _enabled = enabled;
        if (!enabled) return;
        _scope = _cfg.GetCVar(RecoveryCVars.Scope);
        CrashRecoveryOperations.Prefix(_scope);
        BeginRun();
    }

    private void BeginRun()
    {
        if (_stopping) return;
        _run = Guid.NewGuid();
        _closing = false;
        _open = false;
        _boot = _db.BootCrashRecovery(_scope, _run);
        _signalTarget = new(_scope, _run, _boot);
    }

    private void OnSpawn(PlayerSpawnCompleteEvent args)
        => PreparePlayerSpawn(args.Mob, args.Player, args.Profile);

    /// <summary>
    /// Capture the character slot before starting gear payments replace its cached profile.
    /// Resolving its immutable database identity remains asynchronous.
    /// </summary>
    public void PreparePlayerSpawn(EntityUid body, ICommonSession? player, ICharacterProfile? profile)
    {
        if (!_enabled || player == null || profile == null || _profiles.ContainsKey(body) || HasComp<RecoveryBodyComponent>(body)) return;
        if (HasComp<BitrunAvatarComponent>(body) || !_preferences.TryGetCachedPreferences(player.UserId, out var prefs)) return;
        var slot = prefs.IndexOfCharacter(profile);
        if (slot < 0) return;
        _profiles[body] = _db.GetRecoveryProfile(player.UserId.UserId, slot);
        _profileUsers[body] = player.UserId.UserId;
        _profileSlots[body] = slot;
    }

    private void OnEntityInitialized(Entity<MetaDataComponent> entity)
    {
        if (!_enabled) return;
        if (_spawnPayment != null && HasComp<ItemComponent>(entity)) AttributeSpawnedItem(entity);
        if (HasComp<Content.Shared.Research.Components.ResearchServerComponent>(entity)) ResearchServerStarted(entity);
    }

    public void MindBodyChanged(EntityUid mind, EntityUid body)
    {
        if (!_enabled || HasComp<GhostComponent>(body) || HasComp<BitrunAvatarComponent>(body)) return;
        if (TryComp<RecoveryBodyComponent>(body, out var existing))
            _mindProfiles[mind] = (existing.User, existing.Profile, existing.Slot);
        else if (_mindProfiles.TryGetValue(mind, out var character))
            RegisterBody(body, character.User, character.Profile, character.Slot);
    }

    public void RegisterBody(EntityUid body, Guid user, int profile, int slot = -1)
    {
        if (!_enabled || profile <= 0) return;
        if (_deferredEscrows.Remove(body, out var waiting))
            foreach (var escrow in waiting)
                if (!TerminatingOrDeleted(escrow))
                {
                    var owner = EnsureComp<RecoveryEscrowComponent>(escrow);
                    owner.User = user; owner.Profile = profile;
                    _discover.Enqueue(escrow, 65536);
                }
        if (TerminatingOrDeleted(body) || HasComp<BitrunAvatarComponent>(body)) return;
        var marker = EnsureComp<RecoveryBodyComponent>(body);
        marker.User = user;
        marker.Profile = profile;
        marker.Slot = slot;
        if (!marker.Registered)
        {
            marker.Registered = true;
            if (TryComp<ImplantedComponent>(body, out var implants))
                marker.StartingImplants.UnionWith(implants.ImplantContainer.ContainedEntities);
        }
        if (TryComp<MindContainerComponent>(body, out var mind) && mind.Mind is { } mindId)
            _mindProfiles[mindId] = (user, profile, slot);
        if (!_bodies.Contains(body)) _bodies.Add(body);
        _discover.Enqueue(body, 65536);
        QueueDelivery(body, user, profile);
    }

    public void RegisterEscrow(EntityUid escrow, EntityUid body)
    {
        if (!_enabled) return;
        var owner = EnsureComp<RecoveryEscrowComponent>(escrow);
        if (TryComp<RecoveryBodyComponent>(body, out var character))
        {
            owner.User = character.User; owner.Profile = character.Profile;
        }
        else
        {
            if (!_deferredEscrows.TryGetValue(body, out var roots)) _deferredEscrows[body] = roots = new();
            roots.Add(escrow);
        }
        if (!_escrowRoots.Contains(escrow)) _escrowRoots.Add(escrow);
        _discover.Enqueue(escrow, 65536);
    }

    private void OnParentChanged(ref EntParentChangedMessage args)
    {
        if (!_open || _closing || _suppressDeletion) return;
        // Starting/stopping a sound must not dirty its owning item or enqueue another subtree scan.
        if (IsRecoveryAudio(args.Entity)) return;
        // The transform callback only queues work. Descendants are walked later under the global budget.
        if (_assets.ContainsKey(args.Entity)) _dirty.Enqueue(args.Entity, 65536);
        var parent = args.Transform.ParentUid;
        if (_assets.ContainsKey(parent))
        {
            if (!_discoveredEntities.Contains(args.Entity))
            {
                _childrenDiscovered.Remove(parent);
                _discover.Enqueue(parent, 65536);
            }
            _dirty.Enqueue(parent, 65536);
        }
        if (_assets.ContainsKey(args.Entity) || HasComp<RecoveryBodyComponent>(args.Entity) || FindOwner(args.Entity) != null)
            _discover.Enqueue(args.Entity, 65536);
    }

    private void OnItemTerminating(Entity<RecoveryIdentityComponent> ent, ref EntityTerminatingEvent args)
    {
        if (!_open || _closing || _suppressDeletion || !_assets.TryGetValue(ent, out var old)) return;
        if (old.Location is RecoveryLocation.Destroyed or RecoveryLocation.Persistent) return;
        var update = old with { Revision = ++_revision, Location = RecoveryLocation.Destroyed, Data = "" };
        _assets[ent] = update;
        Stage(update);
    }

    private (Guid User, int Profile, string[] Path)? FindOwner(EntityUid entity)
    {
        var path = new List<string>();
        var current = entity;
        for (var depth = 0; depth < 64 && !TerminatingOrDeleted(current); depth++)
        {
            if (TryComp<RecoveryBodyComponent>(current, out var body))
            {
                path.Reverse();
                return (body.User, body.Profile, path.ToArray());
            }
            if (TryComp<RecoveryEscrowComponent>(current, out var escrow))
            {
                if (escrow.Profile <= 0) return null;
                path.Reverse();
                var root = entity;
                while (Transform(root).ParentUid != current && Transform(root).ParentUid.IsValid()) root = Transform(root).ParentUid;
                var prefix = escrow.ReturnPaths.GetValueOrDefault(root);
                return (escrow.User, escrow.Profile, prefix == null ? path.ToArray() : prefix.Concat(path.Skip(1)).ToArray());
            }
            if (_containers.TryGetContainingContainer(current, out var container)) path.Add(container.ID);
            var parent = Transform(current).ParentUid;
            if (!parent.IsValid()) break;
            current = parent;
        }
        return null;
    }

    private bool Protectable(EntityUid uid) => !HasComp<ApartmentChameleonGearComponent>(uid)
        && !HasComp<BitrunVirtualEntityComponent>(uid) && !HasComp<BitrunAvatarComponent>(uid)
        && (HasComp<SubdermalImplantComponent>(uid) || HasComp<ItemComponent>(uid) && !HasComp<BodyPartComponent>(uid) && !HasComp<OrganComponent>(uid)
            || HasComp<OrganComponent>(uid) && HasComponentNamed(uid, "Cybernetics")
            || HasComp<BodyPartComponent>(uid) && HasComponentNamed(uid, "Cybernetics"));

    [Dependency] private IComponentFactory _componentFactory = default!;
    private bool HasComponentNamed(EntityUid uid, string name)
        => _componentFactory.TryGetRegistration(name, out var registration) && EntityManager.HasComponent(uid, registration.Type);

    private bool IsRecoveryAudio(EntityUid uid)
        => HasComp<AudioComponent>(uid) || MetaData(uid).EntityPrototype?.ID == "Audio";

    private void DiscoverOne(EntityUid uid)
    {
        _discoveredEntities.Add(uid);
        if (TerminatingOrDeleted(uid) || HasComp<BitrunVirtualEntityComponent>(uid)) return;
        // Sounds are temporary children of items, not recoverable contents. Skip them before assigning
        // an identity, reserving YAML IDs or walking a helper subtree. Other non-savable roots can
        // still own real equipment, so they must retain the normal child traversal.
        if (IsRecoveryAudio(uid)) return;
        // A purchased grid has a receipt identity, but never starts an equipment subtree scan.
        if (_assets.TryGetValue(uid, out var nonItem) && nonItem.Kind != "item") return;
        // Actions are runtime helpers, regenerated by the restored item's owning system.
        // Capturing their old performer would retain a reference to the previous body.
        if (HasComp<Content.Shared.Actions.InstantActionComponent>(uid)
            || HasComp<Content.Shared.Actions.EntityTargetActionComponent>(uid)
            || HasComp<Content.Shared.Actions.WorldTargetActionComponent>(uid)
            || HasComp<Content.Shared.Actions.EntityWorldTargetActionComponent>(uid)) return;
        var owner = FindOwner(uid);
        var parentTracked = _assets.TryGetValue(Transform(uid).ParentUid, out var ancestor) && ancestor.Kind == "item";
        if (!HasComp<ApartmentChameleonGearComponent>(uid) && !HasComp<RecoveryEscrowComponent>(uid)
            && !HasComp<RecoveryBodyComponent>(uid) && (Protectable(uid) || parentTracked)
            && (owner != null || _assets.ContainsKey(uid) || parentTracked))
        {
            if (!_assets.ContainsKey(uid) && (owner != null || ancestor != null))
            {
                if (_assets.Count >= 65536) return;
                var found = owner ?? (User: ancestor!.User, Profile: ancestor.Profile, Path: Array.Empty<string>());
                var id = EnsureComp<RecoveryIdentityComponent>(uid).Identity;
                // A duplicate identity is quarantined instead of silently manufacturing a second entitlement.
                if (_entitiesByIdentity.TryGetValue(id, out var other) && other != uid && !Deleted(other)) return;
                _entitiesByIdentity[id] = uid;
                var state = new RecoveryAsset(id, ++_revision, found.User, found.Profile, ++_nextYaml,
                    MetaData(uid).EntityPrototype?.ID ?? "", owner == null ? RecoveryLocation.Loose : RecoveryLocation.Carried, parentTracked ? ancestor!.Identity : null, found.Path, "");
                _assets.Add(uid, state);
                _tracked.Add(uid);
                _serializer?.YamlUidMap.TryAdd(uid, state.YamlId);
            }
            // The async profile lookup can finish after an initial, unowned purchase capture.
            // Adding the body's owner marker does not modify the item's metadata tick.
            var ownershipChanged = owner is { } character && _assets.TryGetValue(uid, out var recorded)
                && (recorded.User != character.User || recorded.Profile != character.Profile || recorded.Location == RecoveryLocation.Loose);
            if (ownershipChanged || !_capturedTicks.TryGetValue(uid, out var captured) || captured != MetaData(uid).EntityLastModifiedTick)
                _dirty.Enqueue(uid, 65536);
        }
        // A single pathological backpack can have thousands of children. Even its enumeration
        // is resumed one edge at a time instead of bypassing the per-tick budget.
        if (Transform(uid).ChildCount > 0 && _scanningChildren.Count < 65536 && _scanningChildren.Add(uid))
        {
            var scan = new ChildScan(uid, Transform(uid).ChildEnumerator);
            // A small bounded ID snapshot survives container churn between frames.
            // Large trees retain the incremental enumerator and its global work budget.
            if (Transform(uid).ChildCount <= 64)
            {
                var children = new List<EntityUid>(Transform(uid).ChildCount);
                while (scan.Children.MoveNext(out var child)) children.Add(child);
                scan.Children.Dispose();
                scan.StableChildren = children.ToArray();
            }
            // Equipment references must not wait behind the body's unrelated organ/action tree.
            if (_assets.ContainsKey(uid)) _equipmentScans.Enqueue(scan);
            else _childScans.Enqueue(scan);
        }
    }

    private void ScanChild()
    {
        var queue = _equipmentScans.Count > 0 ? _equipmentScans : _childScans;
        if (!queue.TryPeek(out var scan)) return;
        try
        {
            // A parent snapshot may contain dictionaries keyed by its children. Wait for
            // each queued child to receive its YAML identity before finishing this scan.
            if (scan.PendingChild is { } pending && !_discoveredEntities.Contains(pending))
            {
                _discover.Enqueue(pending, 65536);
                return;
            }
            scan.PendingChild = null;
            EntityUid child = default;
            var hasChild = !TerminatingOrDeleted(scan.Parent) && (scan.StableChildren is { } stable
                ? scan.ChildIndex < stable.Length && (child = stable[scan.ChildIndex++]).IsValid()
                : scan.Children.MoveNext(out child));
            if (hasChild)
            {
                _discover.Enqueue(child, 65536);
                scan.PendingChild = child;
                return;
            }
            _childrenDiscovered.Add(scan.Parent);
        }
        catch (InvalidOperationException)
        {
            // Containment changed between frames. Restart this one parent; insertion events
            // already queued the newly moved child independently.
            _discover.Enqueue(scan.Parent, 65536);
        }
        scan.Children.Dispose();
        queue.Dequeue();
        _scanningChildren.Remove(scan.Parent);
    }

    private void CaptureOne(EntityUid uid)
    {
        if (TerminatingOrDeleted(uid) || !_assets.TryGetValue(uid, out var old) || old.Kind != "item") return;
        if (old.Location == RecoveryLocation.Destroyed) return;
        if (Transform(uid).ChildCount > 0 && !_childrenDiscovered.Contains(uid))
        {
            _dirty.Enqueue(uid, 65536);
            return;
        }
        if (_timing.RealTime < _failedUntil.GetValueOrDefault(uid))
        {
            _captureAfter[uid] = _failedUntil[uid];
            return;
        }
        var owner = FindOwner(uid);
        var parent = Transform(uid).ParentUid;
        var current = old with { Revision = ++_revision, Location = owner == null ? RecoveryLocation.Loose : RecoveryLocation.Carried,
            User = owner?.User ?? old.User, Profile = owner?.Profile ?? old.Profile,
            Parent = _assets.TryGetValue(parent, out var p) && p.Kind == "item" ? p.Identity : null, BodyPath = owner?.Path ?? old.BodyPath, Data = "",
            ContainerStore = TryComp<Content.Shared._WF.SafetyDepositBox.Components.SafetyDepositBoxComponent>(uid, out var box) ? box.BoxId : old.ContainerStore };
        _assets[uid] = current;
        if (owner == null) { Stage(current); return; }
        var captureStarted = Stopwatch.GetTimestamp();
        var collections = GC.CollectionCount(0);
        try
        {
            SaveImplantState(uid);
            _serializer ??= NewSerializer();
            _serializer.SerializeEntity(uid);
            var node = _serializer.EntityData[current.YamlId].Node;
            MarkReferences(uid, node);
            // Detach only newly created data nodes, never pass ECS objects to the writer.
            Stage(current, node);
            _capturedTicks[uid] = MetaData(uid).EntityLastModifiedTick;
        }
        catch (Exception e)
        {
            Stage(current); // location remains authoritative; retain last successfully serialized state
            _failedUntil[uid] = _timing.RealTime + TimeSpan.FromMinutes(1);
            _captureAfter[uid] = _failedUntil[uid];
            _serializer = null;
            Log.Error($"Recovery capture failed for {uid}: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(captureStarted).TotalMilliseconds;
            if (elapsed > MaxCaptureMilliseconds)
            {
                MaxCaptureMilliseconds = elapsed;
                MaxCapturePrototype = current.Prototype;
                MaxCaptureCollections = GC.CollectionCount(0) - collections;
            }
            if (_serializer != null)
            {
                _serializer.EntityData.Clear();
                _serializer.Prototypes.Clear();
                _serializer.Orphans.Clear();
                _serializer.Nullspace.Clear();
            }
        }
    }

    private EntitySerializer NewSerializer()
    {
        var serializer = new EntitySerializer(_dependencies, new SerializationOptions
        { Category = FileCategory.Save, MissingEntityBehaviour = MissingEntityBehaviour.Ignore, ErrorOnOrphan = false });
        foreach (var (uid, asset) in _assets) serializer.YamlUidMap[uid] = asset.YamlId;
        return serializer;
    }

    public override void Update(float frameTime)
    {
        if (!_enabled || _stopping) return;
        PollBootAndWriter();
        if (!_open || _closing) return;
        var started = Stopwatch.GetTimestamp();
        var budget = Math.Clamp(_cfg.GetCVar(RecoveryCVars.BudgetMilliseconds), .1f, 3f);
        var limit = Math.Clamp(_cfg.GetCVar(RecoveryCVars.MaxEntitiesPerTick), 1, 32);
        var now = _timing.RealTime;
        PollProfiles();
        // Expensive deserialization is restricted to the lobby; late join only moves already prepared entities.
        if (_ticker.RunLevel == GameRunLevel.PreRoundLobby)
        {
            if (!_warmed) { WarmupOne(); StartWriter(_timing.RealTime); return; }
            PrepareRecovery();
        }
        ProcessDelivery();
        var rootCount = _bodies.Count + _escrowRoots.Count;
        if (now >= _nextBodyScan && rootCount > 0 && _dirty.Count == 0 && _discover.Count == 0 && _childScans.Count == 0 && _equipmentScans.Count == 0)
        {
            _nextBodyScan = now + TimeSpan.FromMilliseconds(50);
            if (_bodyCursor >= rootCount) _bodyCursor = 0;
            var root = _bodyCursor < _bodies.Count ? _bodies[_bodyCursor] : _escrowRoots[_bodyCursor - _bodies.Count];
            _bodyCursor++;
            _discover.Enqueue(root, 65536);
        }
        for (var i = 0; i < limit && _pending.Count < Math.Clamp(_cfg.GetCVar(RecoveryCVars.MaxPending), 64, 16384); i++)
        {
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= budget) break;
            // Continuous container events must not starve snapshots or child traversal.
            // Keep the cursor across ticks so an exhausted time budget is fair too.
            for (var lane = 0; lane < 3; lane++)
            {
                var work = _workCursor;
                _workCursor = (_workCursor + 1) % 3;
                if (work == 0 && _dirty.TryDequeue(out var dirty)) { CaptureOne(dirty); break; }
                if (work == 1 && _discover.TryDequeue(out var discover)) { DiscoverOne(discover); break; }
                if (work == 2 && (_childScans.Count > 0 || _equipmentScans.Count > 0)) { ScanChild(); break; }
            }
            PollTracked(now);
        }
        PollResearch();
        LastTickMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        MaxTickMilliseconds = Math.Max(MaxTickMilliseconds, LastTickMilliseconds);
        StartWriter(now);
        if (now >= _nextMetrics)
        {
            _nextMetrics = now + TimeSpan.FromMinutes(1);
            Log.Info($"Recovery: tracked={TrackedCount} pending={PendingCount} tick_ms={LastTickMilliseconds:F3} max_tick_ms={MaxTickMilliseconds:F3} write_failures={_writeFailures} dirty={_dirty.Count} discover={_discover.Count} scans={_equipmentScans.Count}/{_childScans.Count} child_ready={_childrenDiscovered.Count}");
        }
    }

    private readonly Dictionary<EntityUid, TimeSpan> _captureAfter = new();
    private void PollTracked(TimeSpan now)
    {
        if (_tracked.Count == 0) return;
        if (_pollCursor >= _tracked.Count) _pollCursor = 0;
        var uid = _tracked[_pollCursor++];
        if (now < _captureAfter.GetValueOrDefault(uid)) return;
        _captureAfter[uid] = now + TimeSpan.FromSeconds(Math.Max(15, _cfg.GetCVar(RecoveryCVars.CheckpointSeconds)));
        _dirty.Enqueue(uid, 65536);
    }

    private void PollProfiles()
    {
        // At most one completed lookup per tick; session state is deliberately not consulted.
        foreach (var (body, task) in _profiles)
        {
            if (!task.IsCompleted) continue;
            var user = _profileUsers[body];
            var slot = _profileSlots.GetValueOrDefault(body, -1);
            if (!task.IsCompletedSuccessfully)
            {
                if (!_profileRetryAt.TryGetValue(body, out var retry))
                {
                    _profileRetryAt[body] = _timing.RealTime + TimeSpan.FromSeconds(10);
                    Log.Warning($"Recovery profile lookup deferred: {task.Exception?.GetBaseException().Message}");
                }
                else if (_timing.RealTime >= retry)
                {
                    _profiles[body] = _db.GetRecoveryProfile(user, slot);
                    _profileRetryAt.Remove(body);
                }
                continue;
            }
            _profiles.Remove(body);
            _profileUsers.Remove(body);
            _profileSlots.Remove(body);
            if (task.IsCompletedSuccessfully && task.GetAwaiter().GetResult() is { } profile) RegisterBody(body, user, profile, slot);
            else Log.Warning("Cannot resolve immutable recovery profile; body remains pending registration.");
            break;
        }
    }

    private void EndRound()
    {
        _ = CloseNormally();
        _assets.Clear(); _entitiesByIdentity.Clear(); _tracked.Clear(); _bodies.Clear();
        _escrowRoots.Clear(); _deferredEscrows.Clear();
        _dirty.Clear(); _discover.Clear(); _captureAfter.Clear(); _profiles.Clear(); _profileUsers.Clear();
        _profileSlots.Clear();
        _profileRetryAt.Clear();
        _mindProfiles.Clear();
        while (_childScans.TryDequeue(out var scan)) scan.Children.Dispose();
        while (_equipmentScans.TryDequeue(out var equipment)) equipment.Children.Dispose();
        _scanningChildren.Clear(); _workCursor = 0;
        _discoveredEntities.Clear(); _childrenDiscovered.Clear();
        _serializer = null; _nextYaml = 0; _revision = 0;
        _capturedTicks.Clear(); _failedUntil.Clear(); _purchaseDeliveries.Clear();
        _bundles.Clear(); _restore.Clear(); _deliveries.Clear(); _recoveryMap = null;
        ResetResearch();
        _restartRun = true;
    }
}

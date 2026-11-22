using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.Research.Disk;
using Content.Server.Storage.Components;
using Content.Server._NF.Bank;
using Content.Shared.GameTicking;
using Content.Shared.Implants;
using Content.Shared.Implants.Components;
using Content.Shared.Preferences;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Components;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Events;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.Server._DeepLagoon.CrashRecovery;

public sealed partial class CrashRecoverySystem
{
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedSubdermalImplantSystem _implants = default!;
    [Dependency] private BankSystem _bank = default!;
    private sealed class Bundle(RecoveryIncident incident, Guid user, int profile, RecoveryAsset[] assets)
    {
        public RecoveryIncident Incident = incident;
        public Guid User = user;
        public int Profile = profile;
        public RecoveryAsset[] Assets = assets;
        public Dictionary<Guid, EntityUid> Entities = new();
        public bool Prepared;
        public bool Failed;
        public Task<RecoveryClaim>? Claim;
        public bool Queued;
        public bool DeliveryInitialized;
        public Guid[] Refunds = [];
        public Guid[] AssetIds = [];
        public EntityUid Body;
        public Queue<RecoveryAsset> Roots = new();
        public EntityUid? Crate;
        public TimeSpan RetryAt;
    }
    private readonly Queue<Bundle> _restore = new();
    private readonly List<Bundle> _bundles = new();
    private readonly Queue<Bundle> _deliveries = new();
    private EntityUid? _recoveryMap;
    private HashSet<string>? _recoveryReadPrototypes;

    private void InitializeRecoveryReader()
        => SubscribeLocalEvent<BeforeEntityReadEvent>(OnRecoveryRead,
            after: [typeof(Content.Server.Maps.MapMigrationSystem), typeof(Content.Server.Holiday.HolidaySystem)]);

    private void OnRecoveryRead(BeforeEntityReadEvent args)
    {
        var prototypes = _recoveryReadPrototypes;
        _recoveryReadPrototypes = null; // Only this read, never a nested or subsequent map load.
        if (prototypes == null) return;
        foreach (var prototype in prototypes)
        {
            // Map migrations can delete prototypes that still spawn in current loadouts.
            // A crash snapshot restores that valid runtime item, including its actual state.
            if (!_prototypeManager.HasIndex<EntityPrototype>(prototype)) continue;
            args.DeletedPrototypes.Remove(prototype);
            args.RenamedPrototypes.Remove(prototype);
        }
    }

    public bool TryLoadRecoveryEntities(RecoveryAsset[] assets, out LoadResult? loaded)
    {
        using var reader = new StringReader(BuildDocument(assets));
        _recoveryReadPrototypes = assets.Select(a => a.Prototype).Where(p => p.Length > 0).ToHashSet();
        try
        {
            return _loader.TryLoadGeneric(reader, "crash-recovery", out loaded,
                new MapLoadOptions { DeserializationOptions = new DeserializationOptions { StoreYamlUids = true, LogInvalidEntities = false } });
        }
        finally { _recoveryReadPrototypes = null; }
    }

    private void LoadIncidents(RecoveryBoot boot)
    {
        foreach (var incident in boot.Incidents)
        {
            bool Persisted(Guid id) => EntityManager.System<Content.Server._DeepLagoon.ShuttleParking.ParkingSystem>().ContainsRecoveryIdentity(id)
                || EntityManager.System<Content.Server._DeepLagoon.Apartments.ApartmentSystem>().ContainsRecoveryIdentity(id);
            var known = incident.Assets.ToDictionary(a => a.Identity);
            // The bank receipt can commit before the first item-location batch. Persistence must
            // still win if that newly delivered product was already saved by another durable store.
            foreach (var purchase in incident.Purchases)
                foreach (var product in purchase.Products)
                    if (!known.ContainsKey(product) && Persisted(product))
                        known[product] = new RecoveryAsset(product, 0, purchase.User, purchase.Profile, 0, "",
                            RecoveryLocation.Persistent, null, [], "", "persistent-product");
            var verified = incident with { Assets = known.Values.Select(a =>
                a.Location == RecoveryLocation.Persistent || Persisted(a.Identity)
                    ? a with { Location = RecoveryLocation.Persistent } : a).ToArray() };
            foreach (var group in verified.Assets.Where(a => a.Location == RecoveryLocation.Carried)
                         .GroupBy(a => (a.User, a.Profile)))
            {
                var bundle = new Bundle(verified, group.Key.User, group.Key.Profile, group.ToArray());
                _restore.Enqueue(bundle);
                _bundles.Add(bundle);
            }
            foreach (var group in incident.Purchases.Where(p => CrashRecoveryOperations.Refundable(p, verified.Assets)).GroupBy(p => (p.User, p.Profile)))
                if (!_bundles.Any(b => b.Incident.Run.Identity == incident.Run.Identity && b.User == group.Key.User && b.Profile == group.Key.Profile))
                {
                    var bundle = new Bundle(verified, group.Key.User, group.Key.Profile, []);
                    _restore.Enqueue(bundle);
                    _bundles.Add(bundle);
                }
        }
    }

    private EntityUid RecoveryMap()
    {
        if (_recoveryMap is { } existing && !Deleted(existing)) return existing;
        var map = _maps.CreateMap();
        _maps.SetPaused(map, true);
        _recoveryMap = map;
        return map;
    }

    private void PrepareRecovery()
    {
        if (!_restore.TryDequeue(out var bundle)) return;
        try
        {
            var missing = bundle.Assets.Where(a => a.Kind == "item" && (a.Data.Length == 0
                || a.Prototype.Length > 0 && !_prototypeManager.HasIndex<EntityPrototype>(a.Prototype))).ToArray();
            if (missing.Length > 0)
            {
                Log.Warning($"Recovery retains {missing.Length} incomplete item records for profile={bundle.Profile}; other items remain recoverable.");
                bundle.Assets = bundle.Assets.Except(missing).ToArray();
            }
            var items = bundle.Assets.Where(a => a.Kind == "item").ToArray();
            if (items.Length > 8192) throw new InvalidDataException("Recovery bundle exceeds entity limit.");
            if (items.Length > 0)
            {
                if (!TryLoadRecoveryEntities(items, out var loaded))
                    throw new InvalidDataException("Recovery deserialization failed.");
                var byYaml = items.Select((asset, index) => (asset, index)).ToDictionary(pair => pair.index + 1, pair => pair.asset);
                foreach (var uid in loaded!.Entities)
                {
                    if (!TryComp<YamlUidComponent>(uid, out var yaml) || !byYaml.TryGetValue(yaml.Uid, out var asset)) continue;
                    EnsureComp<RecoveryIdentityComponent>(uid).Identity = asset.Identity;
                    bundle.Entities[asset.Identity] = uid;
                }
                if (bundle.Entities.Count != items.Length)
                    throw new InvalidDataException($"Recovery entity count mismatch: {bundle.Entities.Count}/{items.Length}; missing: {string.Join(", ", items.Where(a => !bundle.Entities.ContainsKey(a.Identity)).Select(a => a.Prototype).Distinct())}");
                foreach (var asset in items.Where(a => a.Parent == null || !bundle.Entities.ContainsKey(a.Parent.Value)
                             || !Transform(bundle.Entities[a.Identity]).ParentUid.IsValid()))
                    _transform.SetCoordinates(bundle.Entities[asset.Identity], new EntityCoordinates(RecoveryMap(), 0, 0));
            }
            foreach (var research in bundle.Assets.Where(a => a.Kind == "research" && a.Amount > 0))
            {
                var disk = Spawn("ResearchDisk", new EntityCoordinates(RecoveryMap(), 0, 0));
                Comp<ResearchDiskComponent>(disk).Points = research.Amount;
                EnsureComp<Content.Shared.Cargo.Components.StaticPriceComponent>(disk).Price = research.Amount / 5.0;
                EnsureComp<RecoveryIdentityComponent>(disk).Identity = research.Identity;
                var metadata = EntityManager.System<MetaDataSystem>();
                metadata.SetEntityName(disk, $"Компенсация РнД — {research.Amount:N0} очков");
                metadata.SetEntityDescription(disk, "Очки исследований, восстановленные после аварийного завершения сервера.");
                bundle.Entities[research.Identity] = disk;
            }
            bundle.Prepared = true;
            bundle.AssetIds = bundle.Assets.Select(a => a.Identity).ToArray();
            bundle.Refunds = bundle.Incident.Purchases.Where(p => p.User == bundle.User && p.Profile == bundle.Profile
                && CrashRecoveryOperations.Refundable(p, bundle.Incident.Assets)).Select(p => p.Identity).ToArray();
        }
        catch (Exception e)
        {
            bundle.Failed = true;
            _suppressDeletion = true;
            try { foreach (var uid in bundle.Entities.Values) if (!Deleted(uid)) Del(uid); }
            finally { _suppressDeletion = false; }
            bundle.Entities.Clear();
            Log.Error($"Recovery bundle quarantined; incident={bundle.Incident.Run.Identity} profile={bundle.Profile}: {e}");
        }
    }

    /// <summary>Rebuild containment from the current ledger, not from stale backpack snapshots.</summary>
    public static string BuildDocument(RecoveryAsset[] assets)
    {
        var byId = assets.ToDictionary(a => a.Identity);
        var yamlIds = assets.Select((asset, index) => (asset.Identity, Id: index + 1)).ToDictionary(pair => pair.Identity, pair => pair.Id);
        var childrenByParent = assets.Where(a => a.Parent != null).ToLookup(a => a.Parent!.Value);
        var nodes = new Dictionary<Guid, YamlMappingNode>();
        var detached = new HashSet<Guid>();
        var groups = new Dictionary<string, YamlSequenceNode>();
        var roots = new YamlSequenceNode();
        foreach (var asset in assets)
        {
            var yaml = new YamlStream();
            yaml.Load(new StringReader(asset.Data));
            var node = (YamlMappingNode)ResolveReferences(yaml.Documents[0].RootNode, yamlIds);
            nodes.Add(asset.Identity, node);
            node.Children[new YamlScalarNode("uid")] = new YamlScalarNode(yamlIds[asset.Identity].ToString(CultureInfo.InvariantCulture));
            node.Children.Remove(new YamlScalarNode("paused"));
            var hasParent = asset.Parent is { } parent && byId.ContainsKey(parent);
            if (!hasParent) roots.Add(new YamlScalarNode(yamlIds[asset.Identity].ToString(CultureInfo.InvariantCulture)));
            if (node.Children.TryGetValue(new YamlScalarNode("components"), out var componentsNode) && componentsNode is YamlSequenceNode components)
            {
                foreach (var component in components.Children.OfType<YamlMappingNode>().ToArray())
                {
                    var type = component.Children.TryGetValue(new YamlScalarNode("type"), out var t) ? t.ToString() : "";
                    if (type is "Transform")
                    {
                        component.Children[new YamlScalarNode("parent")] = new YamlScalarNode(hasParent ? yamlIds[asset.Parent!.Value].ToString(CultureInfo.InvariantCulture) : "invalid");
                        if (!hasParent) component.Children[new YamlScalarNode("pos")] = new YamlScalarNode("0,0");
                    }
                    if (type == "SubdermalImplant") component.Children.Remove(new YamlScalarNode("action"));
                    // A separately saved backpack can still mention a dropped or transferred item.
                    // Keep UI positions only for children that this incident actually restores here.
                    if (type == "Storage" && component.Children.TryGetValue(new YamlScalarNode("storedItems"), out var storedNode)
                        && storedNode is YamlMappingNode stored)
                    {
                        var children = childrenByParent[asset.Identity].Where(a => a.BodyPath.LastOrDefault() == Content.Shared.Storage.StorageComponent.ContainerId)
                            .Select(a => yamlIds[a.Identity].ToString(CultureInfo.InvariantCulture)).ToHashSet();
                        foreach (var key in stored.Children.Keys.ToArray())
                            if (!children.Contains(key.ToString())) stored.Children.Remove(key);
                    }
                    if (type == "ContainerContainer" && component.Children.TryGetValue(new YamlScalarNode("containers"), out var containersNode) && containersNode is YamlMappingNode containers)
                        foreach (var (id, containerNode) in containers.Children)
                            if (containerNode is YamlMappingNode container)
                            {
                                var contents = new YamlSequenceNode();
                                var slotChildren = childrenByParent[asset.Identity].Where(a => a.BodyPath.LastOrDefault() == id.ToString()).ToArray();
                                foreach (var child in slotChildren)
                                    contents.Add(new YamlScalarNode(yamlIds[child.Identity].ToString(CultureInfo.InvariantCulture)));
                                if (container.Tag == "!type:ContainerSlot" || container.Children.ContainsKey(new YamlScalarNode("ent")))
                                {
                                    // Journal rows can straddle a slot transfer. Keep its last captured
                                    // occupant when available; preserve any other children as loose roots.
                                    var selected = contents.Children.FirstOrDefault();
                                    if (container.Children.TryGetValue(new YamlScalarNode("ent"), out var previous)
                                        && contents.Children.Any(c => c.ToString() == previous.ToString())) selected = previous;
                                    foreach (var child in slotChildren)
                                        if (yamlIds[child.Identity].ToString(CultureInfo.InvariantCulture) != selected?.ToString()) detached.Add(child.Identity);
                                    container.Children.Remove(new YamlScalarNode("ents"));
                                    container.Children[new YamlScalarNode("ent")] = selected ?? new YamlScalarNode("null");
                                }
                                else container.Children[new YamlScalarNode("ents")] = contents;
                            }
                }
            }
            if (!groups.TryGetValue(asset.Prototype, out var group)) groups[asset.Prototype] = group = new YamlSequenceNode();
            group.Add(node);
        }
        foreach (var identity in detached)
        {
            var node = nodes[identity];
            if (node.Children.TryGetValue(new YamlScalarNode("components"), out var componentNodes) && componentNodes is YamlSequenceNode components)
                foreach (var component in components.Children.OfType<YamlMappingNode>())
                    if (component.Children.TryGetValue(new YamlScalarNode("type"), out var type) && type.ToString() == "Transform")
                    {
                        component.Children[new YamlScalarNode("parent")] = new YamlScalarNode("invalid");
                        component.Children[new YamlScalarNode("pos")] = new YamlScalarNode("0,0");
                    }
            roots.Add(new YamlScalarNode(yamlIds[identity].ToString(CultureInfo.InvariantCulture)));
        }
        var entities = new YamlSequenceNode();
        foreach (var (prototype, group) in groups)
            entities.Add(new YamlMappingNode { { "proto", prototype }, { "entities", group } });
        var document = new YamlMappingNode
        {
            { "meta", new YamlMappingNode { { "format", "7" }, { "category", "Save" } } },
            { "maps", new YamlSequenceNode() }, { "grids", new YamlSequenceNode() },
            { "orphans", roots }, { "nullspace", new YamlSequenceNode() },
            { "tilemap", new YamlMappingNode() }, { "entities", entities },
        };
        using var output = new StringWriter();
        new YamlStream(new YamlDocument(document)).Save(new YamlMappingFix(new Emitter(output)), false);
        return output.ToString();
    }

    private void QueueDelivery(EntityUid body, Guid user, int profile)
    {
        foreach (var bundle in _bundles.Where(b => b.User == user && b.Profile == profile && b.Prepared && !b.Failed && !b.Queued))
        {
            bundle.Body = body;
            bundle.Queued = true;
            _deliveries.Enqueue(bundle);
        }
    }

    private void ProcessDelivery()
    {
        if (!_deliveries.TryPeek(out var bundle)) return;
        if (TerminatingOrDeleted(bundle.Body))
        {
            _deliveries.Dequeue();
            bundle.Queued = false;
            return; // another character's claim must not wait for this body's replacement
        }
        if (bundle.Claim == null)
        {
            if (_timing.RealTime < bundle.RetryAt)
            {
                _deliveries.Enqueue(_deliveries.Dequeue());
                return;
            }
            bundle.Claim = _db.ClaimCrashRecovery(_scope, bundle.Incident.Run.Identity, _run, bundle.User, bundle.Profile,
                bundle.AssetIds, bundle.Refunds, _ticker.RoundId);
            return;
        }
        if (bundle.Claim is not { IsCompleted: true } claim) return;
        if (!claim.IsCompletedSuccessfully)
        {
            bundle.Claim = null;
            bundle.RetryAt = _timing.RealTime + TimeSpan.FromSeconds(10);
            _deliveries.Enqueue(_deliveries.Dequeue());
            Log.Error($"Recovery claim deferred without consuming entitlement: {claim.Exception?.GetBaseException().Message}");
            return;
        }
        if (!bundle.DeliveryInitialized)
        {
            bundle.DeliveryInitialized = true;
            var result = claim.GetAwaiter().GetResult();
            foreach (var asset in result.Assets.Where(a => bundle.Entities.TryGetValue(a.Identity, out var uid)
                         && Transform(uid).ParentUid == _recoveryMap)) bundle.Roots.Enqueue(asset);
            if (!TerminatingOrDeleted(bundle.Body)) _bank.SetConfirmedBankBalance(bundle.Body, result.Balance);
            if (TryComp<RecoveryBodyComponent>(bundle.Body, out var body) && body.Slot >= 0 &&
                _preferences.TryGetCachedPreferences(new NetUserId(bundle.User), out var prefs) &&
                prefs.Characters.TryGetValue(body.Slot, out var selected) && selected is HumanoidCharacterProfile profile)
                _ = _preferences.SetProfile(new NetUserId(bundle.User), body.Slot, profile.WithBankBalance(result.Balance), persist: false);
        }
        if (TerminatingOrDeleted(bundle.Body)) return; // claimed assets remain on the private map and in the new run's journal
        if (bundle.Roots.TryDequeue(out var root) && bundle.Entities.TryGetValue(root.Identity, out var uid))
        {
            var path = root.Kind == "research" ? Array.Empty<string>() : root.BodyPath;
            var target = bundle.Body;
            var placed = false;
            if (path.Length == 1 && path[0] == ImplanterComponent.ImplantSlotId && TryComp<SubdermalImplantComponent>(uid, out var implant))
            {
                if (TryComp<ImplantedComponent>(target, out var installed))
                    foreach (var starting in installed.ImplantContainer.ContainedEntities.ToArray())
                        if (MetaData(starting).EntityPrototype?.ID == root.Prototype
                            && TryComp<RecoveryBodyComponent>(target, out var recipient) && recipient.StartingImplants.Remove(starting))
                        { _implants.ForceRemove(target, starting); break; }
                _implants.ForceImplant(target, uid, implant);
                RestoreImplantState(uid);
                placed = true;
            }
            else
            {
                for (var i = 0; i < path.Length; i++)
                {
                    if (!_containers.TryGetContainer(target, path[i], out var container)) break;
                    if (i == path.Length - 1)
                    {
                        if ((HasComp<Content.Shared.Body.Organ.OrganComponent>(uid) || HasComp<Content.Shared.Body.Part.BodyPartComponent>(uid))
                            && container.ContainedEntities.Count == 1 && !HasComp<RecoveryIdentityComponent>(container.ContainedEntities[0]))
                        {
                            var original = container.ContainedEntities[0];
                            _containers.Remove(original, container, force: true);
                            QueueDel(original);
                        }
                        // Existing spawn gear must not be silently destroyed or overwritten.
                        if (container.ContainedEntities.Count == 0 || container is not ContainerSlot)
                            placed = _containers.Insert(uid, container);
                    }
                    else if (container.ContainedEntities.Count == 1) target = container.ContainedEntities[0];
                    else break;
                }
            }
            if (!placed)
            {
                // A personal escrow remains protected and follows this character, even if slots are occupied.
                var holder = bundle.Crate is { } existing && !Deleted(existing) ? existing : Spawn("CrateGenericSteel", Transform(bundle.Body).Coordinates);
                bundle.Crate = holder;
                EntityManager.System<MetaDataSystem>().SetEntityName(holder, "Восстановленное имущество: " + Name(bundle.Body));
                RegisterEscrow(holder, bundle.Body);
                var escrow = EnsureComp<RecoveryEscrowComponent>(holder);
                escrow.User = bundle.User; escrow.Profile = bundle.Profile;
                escrow.ReturnPaths[uid] = path;
                _containers.Insert(uid, Comp<EntityStorageComponent>(holder).Contents);
            }
            // Register the whole loaded subtree incrementally; preserve the immutable item identities.
            _discover.Enqueue(uid, 65536);
            if (bundle.Roots.Count > 0) return;
        }
        _deliveries.Dequeue();
        bundle.Failed = true; // completed, cannot queue the same incident again in this process
    }
}

using System.Linq;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;

namespace Content.Server._DeepLagoon.CrashRecovery;

public sealed partial class CrashRecoverySystem
{
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    private readonly Queue<IComponent> _warmup = new();
    private bool _warmed;
    private int _warmEntity;
    private static readonly string[] WarmEntities = ["ResearchDisk", "ClothingBackpack", "StorageImplant"];

    private void QueueWarmup()
    {
        if (_warmed || _warmup.Count != 0) return;
        // Compile serializer delegates in the lobby, never on the first player's capture.
        // Prototypes are only read here. Warmup never writes player assets or entitlements.
        var types = new HashSet<Type>();
        foreach (var prototype in _prototypeManager.EnumeratePrototypes<EntityPrototype>())
        {
            if (!prototype.Components.ContainsKey("Item") && !prototype.Components.ContainsKey("SubdermalImplant")) continue;
            foreach (var entry in prototype.Components.Values)
                if (types.Add(entry.Component.GetType())) _warmup.Enqueue(entry.Component);
        }
        _warmup.Enqueue(new RecoveryImplantStateComponent());
        Log.Info($"Recovery lobby warmup queued {types.Count} component serializers.");
    }

    private void WarmupOne()
    {
        if (!_warmup.TryDequeue(out var component))
        {
            if (_warmEntity < WarmEntities.Length) WarmupEntity(WarmEntities[_warmEntity++]);
            else
            {
                _warmed = true;
                MaxCaptureMilliseconds = 0;
                MaxCapturePrototype = "";
                MaxCaptureCollections = 0;
            }
            return;
        }
        try
        {
            _serializer ??= NewSerializer();
            _referenceContext ??= new(this);
            _serialization.WriteValueAs<MappingDataNode>(component.GetType(), component, alwaysWrite: true, context: _serializer);
            _serialization.WriteValueAs<MappingDataNode>(component.GetType(), component, alwaysWrite: true, context: _referenceContext);
        }
        catch (Exception error)
        {
            // Some components require a live entity. Their normal capture retains the regular backoff.
            Log.Debug($"Recovery prototype warmup skipped {component.GetType().Name}: {Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(error)}");
        }
    }

    private void WarmupEntity(string prototype)
    {
        var uid = Spawn(prototype, new Robust.Shared.Map.EntityCoordinates(RecoveryMap(), 0, 0));
        var identity = EnsureComp<RecoveryIdentityComponent>(uid).Identity;
        try
        {
            _serializer ??= NewSerializer();
            var yaml = ++_nextYaml;
            _serializer.YamlUidMap[uid] = yaml;
            // Exercise the complete capture path, including runtime-only implant state. This
            // private lobby probe is removed from every queue before the writer can start.
            EnsureComp<RecoveryBodyComponent>(uid);
            _assets[uid] = new Content.Server.Database.RecoveryAsset(identity, ++_revision, Guid.Empty, 0, yaml,
                prototype, Content.Server.Database.RecoveryLocation.Carried, null, [], "");
            _entitiesByIdentity[identity] = uid;
            _childrenDiscovered.Add(uid); // Isolated probe; its runtime actions are intentionally external.
            CaptureOne(uid);
        }
        finally
        {
            _pending.Remove(identity);
            _assets.Remove(uid); _entitiesByIdentity.Remove(identity);
            _capturedTicks.Remove(uid); _failedUntil.Remove(uid);
            _childrenDiscovered.Remove(uid);
            if (_serializer != null)
            {
                _serializer.EntityData.Clear(); _serializer.Prototypes.Clear();
                _serializer.Orphans.Clear(); _serializer.Nullspace.Clear();
                _serializer.YamlUidMap.Remove(uid);
            }
            Del(uid); // lobby-only isolated entity; never staged or attributed to any player
        }
    }
}

using System.IO;
using System.Linq;
using Content.Server.Database;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Serialization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.Server._DeepLagoon.CrashRecovery;

public sealed partial class CrashRecoverySystem
{
    public bool ExternalDeliveriesReady => _enabled && CanAcceptCriticalChange;
    /// <summary>Build immutable recovery rows before acknowledging a durable virtual-item delivery.</summary>
    public RecoveryExternal? CaptureExternalDelivery(Guid user, int profile, IReadOnlyCollection<EntityUid> entities)
    {
        if (!_enabled) return null;
        if (!CanAcceptCriticalChange || entities.Count is < 1 or > 512) throw new InvalidOperationException("recovery_unavailable");
        var previous = entities.ToDictionary(uid => uid, uid => _assets.GetValueOrDefault(uid));
        try
        {
            foreach (var uid in entities)
            {
                var identity = EnsureComp<RecoveryIdentityComponent>(uid);
                identity.Identity = Guid.NewGuid();
                var state = new RecoveryAsset(identity.Identity, ++_revision, user, profile, ++_nextYaml,
                    MetaData(uid).EntityPrototype?.ID ?? "", RecoveryLocation.Carried, null, [], "");
                _assets[uid] = state;
                _entitiesByIdentity[state.Identity] = uid;
            }
            foreach (var uid in entities)
            {
                var parent = Transform(uid).ParentUid;
                if (entities.Contains(parent)) _assets[uid] = _assets[uid] with { Parent = _assets[parent].Identity };
            }
            var serializer = NewSerializer();
            var captured = new List<RecoveryAsset>();
            foreach (var uid in entities)
            {
                SaveImplantState(uid);
                serializer.SerializeEntity(uid);
                var state = _assets[uid];
                var node = serializer.EntityData[state.YamlId].Node;
                MarkReferences(uid, node);
                using var output = new StringWriter();
                new YamlStream(new YamlDocument(node.ToYaml())).Save(new YamlMappingFix(new Emitter(output)), false);
                captured.Add(state with { Data = output.ToString() });
            }
            return new(_scope, _run, captured.ToArray());
        }
        finally
        {
            foreach (var (uid, old) in previous)
            {
                if (_assets.Remove(uid, out var current)) _entitiesByIdentity.Remove(current.Identity);
                if (old != null) { _assets[uid] = old; _entitiesByIdentity[old.Identity] = uid; }
            }
        }
    }

    /// <summary>Activate normal tracking only after the acknowledgement/recovery transaction commits.</summary>
    public void AcceptExternalDelivery(RecoveryExternal? delivery, IEnumerable<EntityUid> entities)
    {
        if (delivery == null) return;
        var states = delivery.Assets.ToDictionary(a => a.Identity);
        foreach (var uid in entities)
            if (TryComp<RecoveryIdentityComponent>(uid, out var identity) && states.TryGetValue(identity.Identity, out var state))
            {
                _assets[uid] = state;
                _entitiesByIdentity[state.Identity] = uid;
                Track(uid);
                Stage(state);
            }
    }
}

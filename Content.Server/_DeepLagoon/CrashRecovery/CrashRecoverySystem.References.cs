using System.Linq;
using Content.Server.Database;
using Robust.Shared.IoC;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;
using YamlDotNet.RepresentationModel;

namespace Content.Server._DeepLagoon.CrashRecovery;

public sealed partial class CrashRecoverySystem
{
    private const string ReferenceTag = "!recovery-ref";
    private const string ReferencePrefix = "recovery-ref:";
    private const string ExternalReferencePrefix = "recovery-external:";
    private const string ExternalReferenceTag = "!recovery-external";
    [Dependency] private ISerializationManager _serialization = default!;
    private ReferenceContext? _referenceContext;

    private sealed class ReferenceContext : ISerializationContext
    {
        public SerializationManager.SerializerProvider SerializerProvider { get; }
        public bool WritingReadingPrototypes => false;
        public ReferenceContext(CrashRecoverySystem system)
        {
            SerializerProvider = new(system._serialization);
            SerializerProvider.RegisterSerializer(new ReferenceWriter(system));
        }
    }

    /// <summary>
    /// Persist references by immutable identity, not the process-local YAML number. Claimed items
    /// from several incidents can coexist in a new run before their next component snapshot.
    /// </summary>
    private sealed class ReferenceWriter(CrashRecoverySystem system) : ITypeWriter<EntityUid>, ITypeWriter<NetEntity>
    {
        public DataNode Write(ISerializationManager serializationManager, EntityUid value,
            IDependencyCollection dependencies, bool alwaysWrite = false, ISerializationContext? context = null)
        {
            if (!value.IsValid()) return new ValueDataNode("invalid");
            var tracked = system._assets.TryGetValue(value, out var asset) && asset.YamlId > 0;
            var external = ExternalReferencePrefix + value;
            if (tracked) return new ValueDataNode(ReferencePrefix + asset!.Identity.ToString("N"));
            // Different external keys must stay distinct until reference annotation prunes them.
            // Collapsing them to "invalid" here makes dictionaries throw before any item can be saved.
            return new ValueDataNode(external) { Tag = ExternalReferenceTag };
        }

        public DataNode Write(ISerializationManager serializationManager, NetEntity value,
            IDependencyCollection dependencies, bool alwaysWrite = false, ISerializationContext? context = null)
            => Write(serializationManager, system.EntityManager.GetEntity(value), dependencies, alwaysWrite, context);
    }

    private bool TryReference(string value, out Guid identity, out string original)
    {
        original = "";
        identity = default;
        if (!value.StartsWith(ReferencePrefix, StringComparison.Ordinal)
            || !Guid.TryParse(value.AsSpan(ReferencePrefix.Length), out identity)
            || !_entitiesByIdentity.TryGetValue(identity, out var uid) || !_assets.TryGetValue(uid, out var asset)) return false;
        original = asset.YamlId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private DataNode AnnotateReferences(DataNode original, DataNode annotated)
    {
        if (annotated is ValueDataNode { Tag: ExternalReferenceTag }) return new ValueDataNode("invalid");
        if (original is ValueDataNode value && annotated is ValueDataNode reference
            && TryReference(reference.Value, out var identity, out var yaml) && value.Value == yaml)
            return new ValueDataNode(identity.ToString("N")) { Tag = ReferenceTag };
        if (original is MappingDataNode mapping && annotated is MappingDataNode annotations)
            foreach (var (key, annotation) in annotations.Children)
            {
                var isReference = TryReference(key, out _, out var originalKey);
                if (!isReference) originalKey = key;
                if (!mapping.TryGet(originalKey, out var child)) continue;
                var marked = AnnotateReferences(child, annotation);
                if (isReference) mapping.Remove(originalKey);
                mapping[key] = marked;
            }
        else if (original is SequenceDataNode sequence && annotated is SequenceDataNode annotationSequence
                 && sequence.Count == annotationSequence.Count)
            for (var i = 0; i < sequence.Count; i++) sequence[i] = AnnotateReferences(sequence[i], annotationSequence[i]);
        return original;
    }

    private void MarkReferences(EntityUid uid, MappingDataNode entity)
    {
        _referenceContext ??= new(this);
        if (!entity.TryGet<SequenceDataNode>("components", out var components)) return;
        foreach (var node in components.Sequence)
        {
            if (node is not MappingDataNode component || !component.TryGet<ValueDataNode>("type", out var name)) continue;
            var registration = _componentFactory.GetRegistration(name.Value);
            if (!EntityManager.TryGetComponent(uid, registration.Type, out var state)) continue;
            // Keep the engine's original component data, prototype differences, fixture handling
            // and time offsets. This separate context supplies only typed reference annotations.
            var annotations = _serialization.WriteValueAs<MappingDataNode>(registration.Type, state, alwaysWrite: true, context: _referenceContext);
            AnnotateReferences(component, annotations);
        }
    }

    private static YamlNode ResolveReferences(YamlNode node, Dictionary<Guid, int> ids)
    {
        if (node is YamlScalarNode scalar && scalar.Tag == ReferenceTag)
            return new YamlScalarNode(Guid.TryParse(scalar.Value, out var identity) && ids.TryGetValue(identity, out var id)
                ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "invalid");
        if (node is YamlSequenceNode sequence)
            return new YamlSequenceNode(sequence.Children.Select(child => ResolveReferences(child, ids))) { Tag = sequence.Tag };
        if (node is YamlMappingNode mapping)
        {
            var result = new YamlMappingNode { Tag = mapping.Tag };
            foreach (var (key, value) in mapping.Children)
            {
                // References used as dictionary keys cannot collapse into several "invalid" keys.
                if (key is YamlScalarNode reference && reference.Tag == ReferenceTag
                    && (!Guid.TryParse(reference.Value, out var identity) || !ids.ContainsKey(identity))) continue;
                if (key is YamlScalarNode referenceKey && referenceKey.Value?.StartsWith(ReferencePrefix, StringComparison.Ordinal) == true)
                {
                    if (Guid.TryParse(referenceKey.Value.AsSpan(ReferencePrefix.Length), out var keyIdentity) && ids.TryGetValue(keyIdentity, out var keyId))
                        result.Add(keyId.ToString(System.Globalization.CultureInfo.InvariantCulture), ResolveReferences(value, ids));
                    continue;
                }
                result.Add(ResolveReferences(key, ids), ResolveReferences(value, ids));
            }
            return result;
        }
        return node;
    }
}

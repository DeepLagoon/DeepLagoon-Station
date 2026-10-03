using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Validation;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Generic;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

namespace Content.Shared.Damage;

//todo writing
public sealed class DamageSpecifierDictionarySerializer : ITypeReader<Dictionary<string, FixedPoint2>, MappingDataNode>
{
    public ValidationNode Validate(ISerializationManager serializationManager, MappingDataNode node,
        IDependencyCollection dependencies, ISerializationContext? context = null)
    {
        var vals = new Dictionary<ValidationNode, ValidationNode>();
        ValidateDict<DamageTypePrototype>(node, "types", dependencies, vals);
        ValidateDict<DamageGroupPrototype>(node, "groups", dependencies, vals);
        return new ValidatedMappingNode(vals);
    }

    private static void ValidateDict<TProto>(MappingDataNode node, string key, IDependencyCollection dependencies,
        Dictionary<ValidationNode, ValidationNode> vals) where TProto : class, IPrototype
    {
        if (!node.TryGet<MappingDataNode>(key, out var sub))
            return;

        var inner = new Dictionary<ValidationNode, ValidationNode>();
        foreach (var (k, v) in sub.Children)
        {
            inner.Add(ProtoIdSerializer<TProto>.Validate(dependencies, new ValueDataNode(k)), new ValidatedValueNode(v));
        }

        vals.Add(new ValidatedValueNode(new ValueDataNode(key)), new ValidatedMappingNode(inner));
    }

    public Dictionary<string, FixedPoint2> Read(ISerializationManager serializationManager, MappingDataNode node, IDependencyCollection dependencies,
        SerializationHookContext hookCtx, ISerializationContext? context = null, ISerializationManager.InstantiationDelegate<Dictionary<string, FixedPoint2>>? instanceProvider = null)
    {
        var dict = instanceProvider != null ? instanceProvider() : new();
        // Add all the damage types by just copying the type dictionary (if it is not null).
        if (node.TryGet<MappingDataNode>("types", out var typesNode))
        {
            serializationManager.Read(typesNode, instanceProvider: () => dict, notNullableOverride: true);
        }

        if (!node.TryGet<MappingDataNode>("groups", out var groupsNode))
            return dict;

        // Then resolve damage groups and add them
        var prototypeManager = dependencies.Resolve<IPrototypeManager>();
        foreach (var entry in serializationManager.Read<Dictionary<string, FixedPoint2>>(groupsNode, notNullableOverride: true))
        {
            if (!prototypeManager.TryIndex<DamageGroupPrototype>(entry.Key, out var group))
            {
                // This can happen if deserialized before prototypes are loaded.
                // i made this a warning bc it was failing tests -paul
                dependencies.Resolve<ILogManager>().RootSawmill.Error($"Unknown damage group given to DamageSpecifier: {entry.Key}");
                continue;
            }

            // Simply distribute evenly (except for rounding).
            // We do this by reducing remaining the # of types and damage every loop.
            var remainingTypes = group.DamageTypes.Count;
            var remainingDamage = entry.Value;
            foreach (var damageType in group.DamageTypes)
            {
                var damage = remainingDamage / FixedPoint2.New(remainingTypes);
                if (!dict.TryAdd(damageType, damage))
                {
                    // Key already exists, add values
                    dict[damageType] += damage;
                }
                remainingDamage -= damage;
                remainingTypes -= 1;
            }
        }

        return dict;
    }
}

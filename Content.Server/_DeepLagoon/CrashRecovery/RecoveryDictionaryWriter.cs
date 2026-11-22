using System.Collections.Frozen;
using System.Linq;
using Robust.Shared.EntitySerialization;
using Robust.Shared.IoC;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Generic;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

namespace Content.Server._DeepLagoon.CrashRecovery;

/// <summary>
/// Recovery saves only owned equipment. References to unrelated entities cannot become dictionary
/// keys: the map serializer would collapse all of them to "invalid" and reject the entire item.
/// This writer is registered only on the recovery entity context, leaving normal map saves intact.
/// </summary>
public sealed class RecoveryDictionaryWriter<TKey, TValue> :
    ITypeWriter<Dictionary<TKey, TValue>>,
    ITypeWriter<IReadOnlyDictionary<TKey, TValue>>,
    ITypeWriter<SortedDictionary<TKey, TValue>>,
    ITypeWriter<FrozenDictionary<TKey, TValue>> where TKey : notnull
{
    private static readonly DictionarySerializer<TKey, TValue> Native = new();

    private static bool Included(TKey key, EntitySerializer serializer)
    {
        var uid = key is EntityUid entity ? entity : serializer.EntMan.GetEntity((NetEntity) (object) key);
        return serializer.YamlUidMap.ContainsKey(uid);
    }

    private static DataNode WriteCore(ISerializationManager manager, IReadOnlyDictionary<TKey, TValue> value,
        IDependencyCollection dependencies, bool alwaysWrite, ISerializationContext? context)
    {
        if ((typeof(TKey) == typeof(EntityUid) || typeof(TKey) == typeof(NetEntity)) && context is EntitySerializer serializer)
        {
            foreach (var key in value.Keys)
            {
                if (Included(key, serializer)) continue;
                value = value.Where(entry => Included(entry.Key, serializer)).ToDictionary();
                break;
            }
        }
        return Native.Write(manager, value, dependencies, alwaysWrite, context);
    }

    public DataNode Write(ISerializationManager manager, Dictionary<TKey, TValue> value,
        IDependencyCollection dependencies, bool alwaysWrite = false, ISerializationContext? context = null)
        => WriteCore(manager, value, dependencies, alwaysWrite, context);

    public DataNode Write(ISerializationManager manager, IReadOnlyDictionary<TKey, TValue> value,
        IDependencyCollection dependencies, bool alwaysWrite = false, ISerializationContext? context = null)
        => WriteCore(manager, value, dependencies, alwaysWrite, context);

    public DataNode Write(ISerializationManager manager, SortedDictionary<TKey, TValue> value,
        IDependencyCollection dependencies, bool alwaysWrite = false, ISerializationContext? context = null)
        => WriteCore(manager, value, dependencies, alwaysWrite, context);

    public DataNode Write(ISerializationManager manager, FrozenDictionary<TKey, TValue> value,
        IDependencyCollection dependencies, bool alwaysWrite = false, ISerializationContext? context = null)
        => WriteCore(manager, value, dependencies, alwaysWrite, context);
}

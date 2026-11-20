using System.Globalization;
using Robust.Shared.IoC;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Validation;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

namespace Content.Shared.Timing;

/// <summary>Persists transient use cooldowns relative to the saved entity's clock.</summary>
public sealed class UseDelayTimestampSerializer : ITypeSerializer<TimeSpan, ValueDataNode>
{
    private const string Prefix = "offset:";
    private static readonly TimeOffsetSerializer Offsets = new();

    public TimeSpan Read(ISerializationManager serializationManager, ValueDataNode node,
        IDependencyCollection dependencies, SerializationHookContext hookCtx,
        ISerializationContext? context = null,
        ISerializationManager.InstantiationDelegate<TimeSpan>? instanceProvider = null)
    {
        // Old snapshots stored absolute server uptime. It cannot be reconstructed
        // after a restart; expire that transient cooldown instead of locking tools.
        if (!node.Value.StartsWith(Prefix, StringComparison.Ordinal))
            return TimeSpan.Zero;
        return Offsets.Read(serializationManager, new ValueDataNode(node.Value.Substring(Prefix.Length)),
            dependencies, hookCtx, context, instanceProvider);
    }

    public ValidationNode Validate(ISerializationManager serializationManager, ValueDataNode node,
        IDependencyCollection dependencies, ISerializationContext? context = null)
    {
        if (node.Value.StartsWith(Prefix, StringComparison.Ordinal))
            return Offsets.Validate(serializationManager, new ValueDataNode(node.Value.Substring(Prefix.Length)), dependencies, context);
        return double.TryParse(node.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out _)
            ? new ValidatedValueNode(node)
            : new ErrorNode(node, "Expected a legacy use timestamp or an offset timestamp.");
    }

    public DataNode Write(ISerializationManager serializationManager, TimeSpan value,
        IDependencyCollection dependencies, bool alwaysWrite = false, ISerializationContext? context = null)
    {
        var offset = (ValueDataNode) Offsets.Write(serializationManager, value, dependencies, alwaysWrite, context);
        return new ValueDataNode(Prefix + offset.Value);
    }
}

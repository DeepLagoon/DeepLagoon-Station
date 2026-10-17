using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Validation;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Implementations;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

namespace Content.Client.Paper.UI;

/// <summary>
/// Paper prototypes historically store margins in Box2 order: left, bottom, right, top.
/// These are independent UI insets, so rectangle bounds validation does not apply.
/// Keep that data format while storing the values as Thickness.
/// </summary>
public sealed class PaperMarginSerializer : ITypeSerializer<Thickness, ValueDataNode>
{
    private static readonly ThicknessSerializer Serializer = new();

    public Thickness Read(ISerializationManager serializationManager, ValueDataNode node,
        IDependencyCollection dependencies, SerializationHookContext hookCtx,
        ISerializationContext? context = null,
        ISerializationManager.InstantiationDelegate<Thickness>? instanceProvider = null)
    {
        var value = Serializer.Read(serializationManager, node, dependencies, hookCtx, context);
        return new Thickness(value.Left, value.Bottom, value.Right, value.Top);
    }

    public DataNode Write(ISerializationManager serializationManager, Thickness value,
        IDependencyCollection dependencies, bool alwaysWrite = false, ISerializationContext? context = null)
    {
        return Serializer.Write(serializationManager,
            new Thickness(value.Left, value.Bottom, value.Right, value.Top), dependencies, alwaysWrite, context);
    }

    public ValidationNode Validate(ISerializationManager serializationManager, ValueDataNode node,
        IDependencyCollection dependencies, ISerializationContext? context = null)
    {
        return Serializer.Validate(serializationManager, node, dependencies, context);
    }
}

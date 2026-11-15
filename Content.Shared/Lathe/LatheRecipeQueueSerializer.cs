using Content.Shared.Research.Prototypes;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Validation;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

namespace Content.Shared.Lathe;

public sealed partial class LatheRecipeQueueSerializer : BaseTypeSerializer,
    ITypeSerializer<List<LatheRecipeBatch>, SequenceDataNode>, ITypeCopyCreator<List<LatheRecipeBatch>>
{
    [DataDefinition]
    public sealed partial class BatchData
    {
        [DataField(required: true)]
        public ProtoId<LatheRecipePrototype> Recipe;

        [DataField]
        public int ItemsPrinted;

        [DataField(required: true)]
        public int ItemsRequested;

        [DataField]
        public EntityUid? Actor;
    }

    public ValidationNode Validate(ISerializationManager serialization, SequenceDataNode node,
        IDependencyCollection dependencies, ISerializationContext? context = null)
    {
        return serialization.ValidateNode<List<BatchData>>(node, context);
    }

    public List<LatheRecipeBatch> Read(ISerializationManager serialization, SequenceDataNode node,
        IDependencyCollection dependencies, SerializationHookContext hookCtx, ISerializationContext? context = null,
        ISerializationManager.InstantiationDelegate<List<LatheRecipeBatch>>? instanceProvider = null)
    {
        var prototypes = dependencies.Resolve<IPrototypeManager>();
        var entities = dependencies.Resolve<IEntityManager>();
        var queue = instanceProvider?.Invoke() ?? new List<LatheRecipeBatch>(node.Count);

        foreach (var entry in node.Sequence)
        {
            var batch = serialization.Read<BatchData>(entry, hookCtx, context, notNullableOverride: true);
            if (!prototypes.TryIndex(batch.Recipe, out var recipe))
            {
                Log.Warning($"Skipping lathe batch for missing recipe '{batch.Recipe}'.");
                continue;
            }

            var actor = entities.TryGetNetEntity(batch.Actor, out var netActor) ? netActor : null;
            // Allocate a new UI dequeue index for the restored batch.
            queue.Add(new LatheRecipeBatch(recipe, batch.ItemsPrinted, batch.ItemsRequested, actor));
        }

        return queue;
    }

    public DataNode Write(ISerializationManager serialization, List<LatheRecipeBatch> value,
        IDependencyCollection dependencies, bool alwaysWrite = false, ISerializationContext? context = null)
    {
        var entities = dependencies.Resolve<IEntityManager>();
        var queue = new SequenceDataNode(value.Count);
        foreach (var batch in value)
        {
            entities.TryGetEntity(batch.Actor, out var actor);
            queue.Add(serialization.WriteValue(new BatchData
            {
                Recipe = batch.Recipe.ID,
                ItemsPrinted = batch.ItemsPrinted,
                ItemsRequested = batch.ItemsRequested,
                Actor = actor,
            }, alwaysWrite, context, notNullableOverride: true));
        }

        return queue;
    }

    public List<LatheRecipeBatch> CreateCopy(ISerializationManager serialization, List<LatheRecipeBatch> source,
        IDependencyCollection dependencies, SerializationHookContext hookCtx, ISerializationContext? context = null)
    {
        var copy = new List<LatheRecipeBatch>(source.Count);
        foreach (var batch in source)
        {
            copy.Add(new LatheRecipeBatch(batch.Recipe, batch.ItemsPrinted, batch.ItemsRequested, batch.Actor)
            {
                Index = batch.Index,
            });
        }

        return copy;
    }
}

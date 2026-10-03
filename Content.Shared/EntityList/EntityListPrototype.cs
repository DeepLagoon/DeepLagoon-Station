using System.Collections.Immutable;
using Robust.Shared.Prototypes;

namespace Content.Shared.EntityList
{
    [Prototype]
    public sealed partial class EntityListPrototype : IPrototype
    {
        [ViewVariables]
        [IdDataField]
        public string ID { get; private set; } = default!;

        [DataField("entities")]
        public List<EntProtoId> EntityIds { get; private set; } = new();

        public IEnumerable<EntityPrototype> Entities(IPrototypeManager? prototypeManager = null)
        {
            prototypeManager ??= IoCManager.Resolve<IPrototypeManager>();

            foreach (var entityId in EntityIds)
            {
                yield return prototypeManager.Index<EntityPrototype>(entityId);
            }
        }
    }
}

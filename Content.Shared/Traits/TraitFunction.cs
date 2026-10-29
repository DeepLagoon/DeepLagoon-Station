using Robust.Shared.Serialization.Manager;

namespace Content.Shared.Traits;

/// <summary>Server-side effects applied after the complete trait selection is validated.</summary>
[ImplicitDataDefinitionForInheritors]
public abstract partial class TraitFunction
{
    public abstract void OnPlayerSpawn(EntityUid mob, IComponentFactory factory,
        IEntityManager entityManager, ISerializationManager serializationManager);
}

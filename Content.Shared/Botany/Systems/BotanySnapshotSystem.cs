using Content.Shared.Botany.Components;

namespace Content.Shared.Botany.Systems;

/// <summary>Copies genetic components into owned snapshot entities using the official engine component-copy API.</summary>
public sealed partial class BotanySnapshotSystem : EntitySystem
{
    public void CloneComponents(EntityUid source, EntityUid target, bool lifecycle)
    {
        foreach (var component in EntityManager.GetComponents(source))
        {
            var type = component.GetType();
            if (type.Namespace?.StartsWith("Content.Shared.Botany") != true
                || component is PlantDataComponent
                || !lifecycle && component is PlantHolderComponent)
                continue;
            EntityManager.CopyComponent(source, target, component);
        }
    }
}

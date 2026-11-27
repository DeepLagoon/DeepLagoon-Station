using Content.Server.Storage.Components;
using Content.Shared.Storage.Components;
using Robust.Shared.GameObjects;

namespace Content.Server._DeepLagoon.BodyInsurance;

public sealed class BodyInsuranceRescueBagSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<BodyInsuranceRescueBagComponent,
            StorageAfterOpenEvent>(OnAfterOpen);
    }

    private void OnAfterOpen(
        Entity<BodyInsuranceRescueBagComponent> ent,
        ref StorageAfterOpenEvent args)
    {
        if (!TryComp<EntityStorageComponent>(ent.Owner, out var storage))
            return;

        // Не удаляем мешок, если внутри что-то осталось.
        if (storage.Contents.ContainedEntities.Count != 0)
            return;

        QueueDel(ent.Owner);
    }
}

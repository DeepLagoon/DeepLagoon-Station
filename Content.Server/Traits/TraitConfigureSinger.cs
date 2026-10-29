using Content.Server.Instruments;
using Content.Shared._DV.Harpy;
using Content.Shared.Instruments;
using Content.Shared.Traits;
using Robust.Shared.Serialization.Manager;

namespace Content.Server.Traits;

/// <summary>Uses the native harpy instrument UI and its speech, stun and muzzle restrictions.</summary>
public sealed partial class TraitConfigureSinger : TraitFunction
{
    [DataField] public bool AllInstruments;

    public override void OnPlayerSpawn(EntityUid uid, IComponentFactory factory,
        IEntityManager entityManager, ISerializationManager serializationManager)
    {
        entityManager.EnsureComponent<InstrumentComponent>(uid, out var instrument);
        entityManager.System<InstrumentSystem>().ConfigureTraitSinger(uid, instrument, AllInstruments);
        entityManager.EnsureComponent<UserInterfaceComponent>(uid, out var ui);
        entityManager.System<SharedUserInterfaceSystem>().SetUi((uid, ui), InstrumentUiKey.Key,
            new InterfaceData("InstrumentBoundUserInterface"));
        entityManager.EnsureComponent<HarpySingerComponent>(uid, out _);
        entityManager.Dirty(uid, instrument);
    }
}

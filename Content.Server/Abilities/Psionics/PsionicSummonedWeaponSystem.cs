using Content.Shared.Abilities.Psionics;
using Content.Shared.Actions.Events;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Events;

namespace Content.Server.Abilities.Psionics;

[RegisterComponent]
public sealed partial class PsionicSummonedWeaponComponent : Component;

public sealed partial class PsionicSummonedWeaponSystem : EntitySystem
{
    [Dependency] private SharedPsionicAbilitiesSystem _psionics = default!;
    [Dependency] private SharedHandsSystem _hands = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<PsionicSummonEquipmentActionEvent>(OnSummon);
        SubscribeLocalEvent<PsionicSummonedWeaponComponent, DroppedEvent>(OnDropped);
    }

    private void OnSummon(PsionicSummonEquipmentActionEvent args)
    {
        if (args.Handled || !_psionics.OnAttemptPowerUse(args.Performer, "Summon Black Blade"))
            return;

        var weapon = Spawn(args.Prototype, Transform(args.Performer).Coordinates);
        if (!_hands.TryPickupAnyHand(args.Performer, weapon))
        {
            QueueDel(weapon);
            return;
        }

        _psionics.LogPowerUsed(args.Performer, "Summon Black Blade");
        args.Handled = true;
    }

    private void OnDropped(EntityUid uid, PsionicSummonedWeaponComponent component, DroppedEvent args)
    {
        QueueDel(uid);
    }
}

using Content.Shared.Interaction.Events;
using Content.Shared.NPC.Systems;
using Content.Shared.Popups;
using Content.Shared.Stunnable;
using Content.Shared.Weapons.Melee.Events;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunHereticBladeComponent : Component;

[RegisterComponent]
public sealed partial class BitrunHereticActorComponent : Component;

/// <summary>Virtual heretic blades retain their source allegiance restriction.</summary>
public sealed class BitrunHereticSystem : EntitySystem
{
    [Dependency] private NpcFactionSystem _factions = default!;
    [Dependency] private SharedStunSystem _stun = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunHereticBladeComponent, AttemptMeleeEvent>(OnAttempt);
        SubscribeLocalEvent<BitrunHereticBladeComponent, UseInHandEvent>(OnUse);
    }

    private void OnAttempt(Entity<BitrunHereticBladeComponent> blade, ref AttemptMeleeEvent args)
    {
        if (args.Cancelled || HasComp<BitrunHereticActorComponent>(args.User))
            return;
        args.Cancelled = true;
        args.Message = Loc.GetString("bitrun-heretic-blade-rejected");
        _stun.TryParalyze(args.User, TimeSpan.FromSeconds(5), true);
    }

    private void OnUse(Entity<BitrunHereticBladeComponent> blade, ref UseInHandEvent args)
    {
        if (args.Handled)
            return;
        // The domain outfit has TRAIT_NO_TELEPORT in WhiteMoon, including
        // the blade's shatter-to-escape activation.
        args.Handled = true;
        if (HasComp<BitrunHereticActorComponent>(args.User))
            _popup.PopupEntity(Loc.GetString("bitrun-heretic-blade-no-escape"), blade, args.User);
        else
            QueueDel(blade);
    }
}

using System.Linq;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Chemistry.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.DoAfter;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.InteractionPanel;

public sealed partial class InteractionPanelSystem
{
    private readonly Dictionary<EntityUid, TimeSpan> _nextManualClimax = new();
    [Dependency] private SharedDoAfterSystem _climaxDoAfter = default!;
    private bool Alive(EntityUid uid) => TryComp<MobStateComponent>(uid, out var state) && state.CurrentState == MobState.Alive;

    private void OnClimaxRequest(InteractionPanelClimaxRequestEvent args, EntitySessionEventArgs session)
        => PerformClimaxRequest(args, session.SenderSession, false);

    private void OnClimaxDoAfter(EntityUid uid, InteractionPanelArousalComponent component, BlueMoonClimaxDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled || !TryComp<ActorComponent>(uid, out var actor)) return;
        args.Handled = true;
        PerformClimaxRequest(args.Request, actor.PlayerSession, true);
    }

    private void PerformClimaxRequest(InteractionPanelClimaxRequestEvent args, ICommonSession session, bool completed)
    {
        if (session.AttachedEntity is not { } user || !ValidPanel(user, session, args.Target)
            || !_panels.TryGetValue(user, out var panel) || !Alive(user) || !_blocker.CanInteract(user, null)
            || Consent(user, InteractionPanelCategory.Erotic) == InteractionPanelConsent.No) return;
        if (args.ClearPregnancy)
        {
            _arousal.EndPregnancy(user);
            return;
        }
        if (!completed && _nextManualClimax.GetValueOrDefault(user) > _timing.CurTime || args.Part is not { Length: <= 16 } || args.Destination is not { Length: <= 16 }
            || !_arousal.ProducingParts(user).Contains(args.Part) || !CanReach(user, panel.Target)) return;
        if (args.Destination is not ("surface" or "mouth" or "vagina" or "anus" or "breasts" or "belly" or "penis" or "nipples" or "urethra")) return;
        EntityUid? container = null;
        if (args.Container)
        {
            container = EntityManager.System<SharedHandsSystem>().GetActiveItem(user);
            if (container == null || !HasComp<RefillableSolutionComponent>(container)) return;
        }
        if (!_arousal.CanExposePart(user, args.Part)) return;
        var target = panel.Target == user || args.Container ? (EntityUid?)null : panel.Target;
        if (target is { } recipient && (!_arousal.Preferences(recipient).ReceiveFluids || !Alive(recipient))) return;
        if (completed) _arousal.TryClimax(user, target, args.Part, args.Destination, container, allParts: false, automaticCycle: false);
        else
        {
            _nextManualClimax[user] = _timing.CurTime + TimeSpan.FromSeconds(3);
            _climaxDoAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, TimeSpan.FromSeconds(3),
                new BlueMoonClimaxDoAfterEvent { Request = args }, user, target, container)
            { NeedHand = true, BreakOnMove = true, BreakOnDamage = true, BreakOnHandChange = true, BlockDuplicate = true });
        }
    }
}

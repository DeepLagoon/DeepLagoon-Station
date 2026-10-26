using System.Linq;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared.Humanoid;

namespace Content.Server._DeepLagoon.InteractionPanel;

public sealed partial class InteractionPanelSystem
{
    private InteractionPanelSexTraits BodySexTraits(EntityUid uid) => TryComp<HumanoidAppearanceComponent>(uid, out var appearance)
        ? InteractionPanelSexRules.GetTraits(appearance.Sex, appearance.BodyFeatures) : InteractionPanelSexTraits.None;

    private void RefreshAvailability(EntityUid changed)
    {
        foreach (var (user, panel) in _panels)
        {
            if (user != changed && panel.Target != changed || panel.Expires <= _timing.CurTime ||
                panel.Session.AttachedEntity != user || !Exists(panel.Target)) continue;
            var actions = InteractionPanelActionCatalog.Load(_prototypes).Where(a => Allowed(user, panel.Target, a)).Select(a => a.Id).ToArray();
            RaiseNetworkEvent(new InteractionPanelAvailabilityEvent(GetNetEntity(panel.Target), actions, BodySexTraits(user), BodySexTraits(panel.Target)), panel.Session);
        }
    }
}

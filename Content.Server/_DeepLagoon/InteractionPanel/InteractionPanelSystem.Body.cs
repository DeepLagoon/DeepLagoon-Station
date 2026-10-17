using System.Linq;
using Content.Server._DeepLagoon.BodyCustomization;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared.Humanoid;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.InteractionPanel;

public sealed partial class InteractionPanelSystem
{
    private void SendBodyState(EntityUid user)
    {
        if (!_panels.TryGetValue(user, out var panel) || panel.Session.AttachedEntity != user) return;
        var features = TryComp<HumanoidAppearanceComponent>(user, out var humanoid)
            ? BodyCustomizationCatalog.Sanitize(humanoid.BodyFeatures).ToArray() : [];
        RaiseNetworkEvent(new InteractionPanelBodyStateEvent(features), panel.Session);
    }

    private void OnBodyVisibility(InteractionPanelBodyVisibilityEvent args, EntitySessionEventArgs session)
    {
        // This tab always controls the attached character, even when looking at someone else.
        if (session.SenderSession.AttachedEntity is not { } user
            || !_panels.TryGetValue(user, out var panel)
            || !Exists(panel.Target)
            || !ValidPanel(user, session.SenderSession, GetNetEntity(panel.Target))) return;
        EntityManager.System<BodyCustomizationSystem>().SetVisibility(user, args.Part, args.Visibility);
    }

    private void OnBodyChanged(EntityUid uid, HumanoidAppearanceComponent component, ref BodyCustomizationChangedEvent args) => SendBodyState(uid);
}

using Content.Shared._DeepLagoon.InteractionPanel;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private void InitializeInteractionPanelPreferences()
    {
        SetupInteractionPanelOption(ERPConsentButton, InteractionPanelCategory.Erotic);
        SetupInteractionPanelOption(NonConConsentButton, InteractionPanelCategory.NonCon);
        SetupInteractionPanelOption(VoreConsentButton, InteractionPanelCategory.Vore);
    }

    private void SetupInteractionPanelOption(OptionButton button, InteractionPanelCategory category)
    {
        foreach (var consent in Enum.GetValues<InteractionPanelConsent>())
            button.AddItem(consent.ToString(), (int) consent);
        button.OnItemSelected += args =>
        {
            if (Profile == null) return;
            button.SelectId(args.Id);
            Profile = Profile.WithInteractionPanelConsent(category, (InteractionPanelConsent) args.Id);
            SetDirty();
        };
    }

    private void UpdateInteractionPanelPreferences()
    {
        ERPConsentButton.Disabled = NonConConsentButton.Disabled = VoreConsentButton.Disabled = Profile == null;
        ERPConsentButton.SelectId((int) (Profile?.ERPConsent ?? InteractionPanelConsent.Ask));
        NonConConsentButton.SelectId((int) (Profile?.NonConConsent ?? InteractionPanelConsent.Ask));
        VoreConsentButton.SelectId((int) (Profile?.VoreConsent ?? InteractionPanelConsent.Ask));
    }
}

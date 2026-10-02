using Content.Shared._DeepLagoon.InteractionPanel;

namespace Content.Shared.Preferences;

public sealed partial class HumanoidCharacterProfile
{
    [DataField] public InteractionPanelConsent ERPConsent { get; private set; } = InteractionPanelConsent.Ask;
    [DataField] public InteractionPanelConsent NonConConsent { get; private set; } = InteractionPanelConsent.Ask;
    [DataField] public InteractionPanelConsent VoreConsent { get; private set; } = InteractionPanelConsent.Ask;

    public InteractionPanelConsent GetInteractionPanelConsent(InteractionPanelCategory category) => category switch
    {
        InteractionPanelCategory.Neutral => InteractionPanelConsent.Yes,
        InteractionPanelCategory.Erotic => ERPConsent,
        InteractionPanelCategory.NonCon => NonConConsent,
        InteractionPanelCategory.Vore => VoreConsent,
        _ => InteractionPanelConsent.No,
    };

    public HumanoidCharacterProfile WithInteractionPanelConsent(InteractionPanelCategory category, InteractionPanelConsent consent)
    {
        var copy = Clone();
        consent = Enum.IsDefined(consent) ? consent : InteractionPanelConsent.No;
        switch (category)
        {
            case InteractionPanelCategory.Erotic: copy.ERPConsent = consent; break;
            case InteractionPanelCategory.NonCon: copy.NonConConsent = consent; break;
            case InteractionPanelCategory.Vore: copy.VoreConsent = consent; break;
        }
        return copy;
    }

    private void CopyInteractionPanelPreferences(HumanoidCharacterProfile other)
    {
        ERPConsent = other.ERPConsent;
        NonConConsent = other.NonConConsent;
        VoreConsent = other.VoreConsent;
    }

    private bool InteractionPanelPreferencesEqual(HumanoidCharacterProfile other) =>
        ERPConsent == other.ERPConsent && NonConConsent == other.NonConConsent && VoreConsent == other.VoreConsent;

    private void ValidateInteractionPanelPreferences()
    {
        if (!Enum.IsDefined(ERPConsent)) ERPConsent = InteractionPanelConsent.No;
        if (!Enum.IsDefined(NonConConsent)) NonConConsent = InteractionPanelConsent.No;
        if (!Enum.IsDefined(VoreConsent)) VoreConsent = InteractionPanelConsent.No;
    }
}

using System.Collections.Generic;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared.Humanoid;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.InteractionPanel;

[Flags, Serializable, NetSerializable]
public enum InteractionPanelSexTraits : byte
{
    None = 0,
    Male = 1,
    Female = 2,
    Unsexed = 4,
}

public static class InteractionPanelSexRules
{
    public static InteractionPanelSexTraits GetTraits(Sex? sex, IEnumerable<BodyFeature>? features = null)
    {
        var traits = sex switch
        {
            Sex.Male => InteractionPanelSexTraits.Male,
            Sex.Female => InteractionPanelSexTraits.Female,
            Sex.Unsexed => InteractionPanelSexTraits.Unsexed,
            _ => InteractionPanelSexTraits.None,
        };
        if (features == null) return traits;
        foreach (var feature in features)
        {
            if (!feature.Enabled) continue;
            traits |= feature.Part switch
            {
                "penis" or "testicles" => InteractionPanelSexTraits.Male,
                "vagina" or "womb" => InteractionPanelSexTraits.Female,
                _ => InteractionPanelSexTraits.None,
            };
        }
        return traits;
    }
}

using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._DeepLagoon.InteractionPanel;

// Arousal belongs to the controlled body, not the account.
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class InteractionPanelArousalComponent : Component
{
    [DataField, AutoNetworkedField] public float Current;
    [DataField, AutoNetworkedField] public float Maximum = 300;
    [DataField, AutoNetworkedField] public TimeSpan RecoveryUntil;
    [DataField, AutoNetworkedField] public int Orgasms = 1;
    [DataField] public BlueMoonPreferences Physiology = new();
    [DataField, AutoNetworkedField] public string[] ProducingParts = [];
    [ViewVariables] public bool PermanentArousal;
    [ViewVariables] public bool NeverAroused;
    [ViewVariables] public bool InHeat;
    [ViewVariables] public TimeSpan NextMoanAttempt;
    [ViewVariables] public bool AutomaticMoans = true;
    [ViewVariables] public float MoanChance = 20;
    [ViewVariables] public float ArousalMultiplier = 100;
}

[Prototype]
public sealed partial class InteractionPanelArousalPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public float HealthMultiplier = 1;
    [DataField] public float FallbackHealth = 100;
    [DataField] public string ResetMessage = "dl-interaction-panel-arousal-reset";
    [DataField] public Color ChatColor = Color.FromHex("#E8A0CF");
    [DataField] public Color VignetteColor = Color.FromHex("#B84B93");
    [DataField] public float DecayPerSecond = 1;
    [DataField] public TimeSpan RecoveryDuration = TimeSpan.FromSeconds(10);
    [DataField] public float VignetteMaxAlpha = 0.65f;
    [DataField] public string[] MaleSounds = Array.Empty<string>();
    [DataField] public string[] FemaleSounds = Array.Empty<string>();
    [DataField] public string[] UnsexedSounds = Array.Empty<string>();
    [DataField] public string[] UnknownSounds = Array.Empty<string>();
}

public static class InteractionPanelArousalRules
{
    public const float MoanThreshold = 0.3f;
    public static readonly TimeSpan MoanInterval = TimeSpan.FromSeconds(5);
    public const float NeutralInteractionGain = 2;

    public static float ClampMoanChance(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 100) : 20;
    public static float ClampMultiplier(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 300) : 100;

    public static float Gain(float amount, float multiplier, bool interaction = false)
    {
        if (!float.IsFinite(amount)) return 0;
        if (amount < 0) return amount;
        var scale = ClampMultiplier(multiplier) / 100;
        return amount == 0 && interaction
            ? NeutralInteractionGain * Math.Max(0, scale - 1)
            : amount * scale;
    }

    public static bool CanMoan(float current, float maximum, bool enabled, float chance) =>
        enabled && ClampMoanChance(chance) > 0 && float.IsFinite(current) && float.IsFinite(maximum)
        && maximum > 0 && current > maximum * MoanThreshold;

    public static float Capacity(float health, float multiplier, float fallback)
    {
        if (!float.IsFinite(fallback) || fallback <= 0) fallback = 100;
        if (!float.IsFinite(health) || health <= 0) health = fallback;
        if (!float.IsFinite(multiplier) || multiplier <= 0) multiplier = 1;
        return Math.Clamp(health * multiplier, 1, 1000000);
    }

    // Переполнение сбрасывается целиком; один запуск даёт максимум один сброс.
    public static bool Add(ref float current, float amount, float maximum)
    {
        if (!float.IsFinite(amount) || !float.IsFinite(maximum) || maximum <= 0) return false;
        current = Math.Clamp(float.IsFinite(current) ? current : 0, 0, maximum);
        if (amount <= 0) { current = Math.Max(0, current + amount); return false; }
        if (amount >= maximum - current) { current = 0; return true; }
        current += amount;
        return false;
    }
}

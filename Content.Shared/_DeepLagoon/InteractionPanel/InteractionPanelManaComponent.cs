using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._DeepLagoon.InteractionPanel;

// Добавляется сервером каждому мобу с MobState. Накопление принадлежит телу, не аккаунту.
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class InteractionPanelManaComponent : Component
{
    [DataField, AutoNetworkedField] public float Current;
    [DataField, AutoNetworkedField] public float Maximum = 100;
}

[Prototype("interactionPanelMana")]
public sealed partial class InteractionPanelManaPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public float HealthMultiplier = 1;
    [DataField] public float FallbackHealth = 100;
    [DataField] public string ResetMessage = "dl-interaction-panel-mana-reset";
    [DataField] public Color ChatColor = Color.FromHex("#80BFFF");
    [DataField] public Color VignetteColor = Color.FromHex("#287FE8");
    [DataField] public float VignetteMaxAlpha = 0.65f;
    [DataField] public string[] MaleSounds = Array.Empty<string>();
    [DataField] public string[] FemaleSounds = Array.Empty<string>();
    [DataField] public string[] UnsexedSounds = Array.Empty<string>();
    [DataField] public string[] UnknownSounds = Array.Empty<string>();
}

public static class InteractionPanelManaRules
{
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
        if (!float.IsFinite(amount) || amount <= 0) return false;
        current = Math.Max(0, float.IsFinite(current) ? current : 0);
        if (amount >= maximum - current) { current = 0; return true; }
        current += amount;
        return false;
    }
}

using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Shared._DeepLagoon.InteractionPanel;

// Настройки блока animation в interaction_panel_actions.yml. Движется только изображение инициатора.
[Serializable, NetSerializable, DataDefinition]
public sealed partial record InteractionPanelAnimationSettings
{
    [DataField] public float Distance = 0.15f; // Дальность движения к цели, в тайлах.
    [DataField] public float SelfDistance = 0.12f; // Смещение на себе.
    [DataField] public float ForwardSeconds = 0.225f;
    [DataField] public float ReturnSeconds = 0.225f;

    public InteractionPanelAnimationSettings() { }

    public InteractionPanelAnimationSettings(float distance, float selfDistance, float forwardSeconds, float returnSeconds)
    {
        Distance = distance;
        SelfDistance = selfDistance;
        ForwardSeconds = forwardSeconds;
        ReturnSeconds = returnSeconds;
    }

    public InteractionPanelAnimationSettings Clamped() => new(
        Safe(Distance, 0, 0.5f, 0.15f), Safe(SelfDistance, 0, 0.5f, 0.12f),
        Safe(ForwardSeconds, 0.05f, 2, 0.225f), Safe(ReturnSeconds, 0.05f, 2, 0.225f));

    private static float Safe(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

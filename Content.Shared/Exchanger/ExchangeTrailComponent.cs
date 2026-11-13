using Robust.Shared.GameStates;

namespace Content.Shared.Exchanger;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ExchangeTrailComponent : Component
{
    [AutoNetworkedField] public EntityUid Target;
    [AutoNetworkedField] public TimeSpan? EndsAt;
    [DataField, AutoNetworkedField] public Color Tint = Color.DeepSkyBlue.WithAlpha(0.8f);
    [DataField, AutoNetworkedField] public float HalfWidth = 0.055f;
    [DataField, AutoNetworkedField] public TimeSpan Fade = TimeSpan.FromSeconds(0.25);
}

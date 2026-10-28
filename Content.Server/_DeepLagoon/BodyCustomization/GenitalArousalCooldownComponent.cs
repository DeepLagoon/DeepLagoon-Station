namespace Content.Server._DeepLagoon.BodyCustomization;

[RegisterComponent]
public sealed partial class GenitalArousalCooldownComponent : Component
{
    [ViewVariables] public TimeSpan NextAutomaticChange;
}

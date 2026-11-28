namespace Content.Server.Explosion.Components;

/// <summary>Transient alert history belongs to the patient, including when several implants are installed.</summary>
[RegisterComponent]
public sealed partial class RattleAlertCooldownComponent : Component
{
    public readonly Dictionary<(string Channel, string Kind), TimeSpan> LastSent = new();
}

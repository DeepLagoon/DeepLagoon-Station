using Content.Server.Explosion.EntitySystems;
using Content.Shared.Sound.Components;

namespace Content.Server.Sound.Components
{
    /// <summary>
    /// Whenever a <see cref="TriggerEvent"/> is run play a sound in PVS range.
    /// </summary>
    [RegisterComponent]
[AutoGenerateComponentState]
    public sealed partial class EmitSoundOnTriggerComponent : BaseEmitSoundComponent
    {
    }
}

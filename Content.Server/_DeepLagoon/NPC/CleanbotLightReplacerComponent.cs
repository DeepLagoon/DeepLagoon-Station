using Robust.Shared.Audio;

namespace Content.Server._DeepLagoon.NPC;

[RegisterComponent]
public sealed partial class CleanbotLightReplacerComponent : Component
{
    [DataField]
    public float Range = 8f;

    [DataField]
    public SoundSpecifier ReplaceSound = new SoundPathSpecifier("/Audio/Weapons/click.ogg")
    {
        Params = new()
        {
            Volume = -4f,
        },
    };
}

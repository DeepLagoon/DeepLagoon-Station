using Robust.Shared.Audio;

namespace Content.Server._NF.Construction.Components;

[RegisterComponent]
public sealed partial class PartExchangerComponent : Component
{
    /// <summary>
    /// How long it takes to exchange the parts
    /// </summary>
    [DataField("exchangeDuration")]
    public float ExchangeDuration = 3;

    /// <summary>
    /// Whether the standard delayed exchange requires normal interaction range.
    /// </summary>
    [DataField("doDistanceCheck")]
    public bool DoDistanceCheck = true;

    /// <summary>Range of the instantaneous, sight-based exchange. Zero uses normal interaction.</summary>
    [DataField] public float RemoteRange;

    [DataField("exchangeSound")]
    public SoundSpecifier ExchangeSound = new SoundPathSpecifier("/Audio/Items/rped.ogg");

    public EntityUid? AudioStream;
}

using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server.Lathe.Components;

/// <summary>
/// For EntityQuery to keep track of which lathes are producing
/// </summary>
[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class LatheProducingComponent : Component
{
    /// <summary>
    /// The time at which production began
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField, ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan StartTime;

    /// <summary>
    /// How long it takes to produce the recipe.
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan ProductionLength;

    /// <summary>The already-paid recipe, independently of the remaining production queue.</summary>
    [DataField]
    public string? Recipe;

    /// <summary>
    /// The Entity that queued this recipe - Mono
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public EntityUid? Actor;

    /// <summary>
    /// The single looping sound played while the lathe is producing.
    /// </summary>
    public EntityUid? ProducingAudio;
}


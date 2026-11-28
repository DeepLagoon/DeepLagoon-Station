using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server._DeepLagoon.Cleanup;

public enum AgeCleanupKind : byte { OpenedMail, SpaceDebris, DefeatedDrone, LooseOrgan }

[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class AgeCleanupComponent : Component
{
    [DataField] public AgeCleanupKind Kind;
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField] public TimeSpan Since;
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField] public TimeSpan Due;
}

/// <summary>Only NPC drone spawners opt in. A player ship is never inferred from its colour or name.</summary>
[RegisterComponent]
public sealed partial class NpcDroneCleanupComponent : Component
{
    [DataField] public bool Defeated;
    public readonly HashSet<EntityUid> Cores = new();
    public bool CoresInitialized;
}

/// <summary>Own lifecycle hook; HTN's exclusive ComponentStartup handler belongs to HTNSystem.</summary>
[RegisterComponent]
public sealed partial class NpcDroneCoreCleanupComponent : Component;

using System.Numerics;
using Robust.Shared.Prototypes;

namespace Content.Server.Movement.Systems;

[RegisterComponent]
public sealed partial class SegmentedTailComponent : Component
{
    [DataField(required: true)] public EntProtoId Segment;
    [DataField] public int Count = 3;
    [DataField] public float Spacing = 1f;
    [DataField] public float Stiffness = 50f;
    [DataField] public float Damping = 2f;
    [DataField] public float FollowStrength = 5f;
    [DataField] public float Smoothing = 10f;
    [DataField] public float MaximumSpeed = 10f;
    [DataField] public bool RotateSegments = true;
    [DataField] public Angle SpriteRotation = Angle.FromDegrees(-90);
    [DataField] public Vector2 RearAnchor = new(-0.15f, 0);
    [DataField] public Vector2 FrontAnchor = new(0.15f, 0);

    [DataField] internal List<EntityUid> Chain = new();
    internal bool Created;
    internal EntityUid? ChainMap;
    internal bool Disposing;
}

[RegisterComponent]
public sealed partial class TailLinkComponent : Component
{
    [DataField] internal EntityUid Head;
}

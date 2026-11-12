namespace Content.Server.Research.Components;

[RegisterComponent]
public sealed partial class ResearchPointSourceComponent : Component
{
    [DataField]
    public int PointsPerSecond;

    [DataField]
    public int ColdBonusPointsPerSecond;

    [DataField]
    public bool Active = true;
}

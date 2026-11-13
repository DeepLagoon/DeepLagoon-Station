namespace Content.Server.Research.Disk;

[RegisterComponent]
public sealed partial class ResearchDiskCollectorComponent : Component
{
    [DataField] public bool Enabled;
    [DataField] public float Range = 1.5f;
    [DataField] public int BatchSize = 15;
    [DataField] public TimeSpan Interval = TimeSpan.FromSeconds(1);
    public TimeSpan NextScan;
}

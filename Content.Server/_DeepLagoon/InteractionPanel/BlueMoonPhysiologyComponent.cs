using Content.Shared._DeepLagoon.BodyCustomization;

namespace Content.Server._DeepLagoon.InteractionPanel;

[RegisterComponent]
public sealed partial class BlueMoonPhysiologyComponent : Component
{
    public readonly Dictionary<string, TimeSpan> LastEmission = new();
    public readonly Dictionary<string, float> GrowthVolumes = new();
    public readonly Dictionary<string, TimeSpan> NextChemicalEffect = new();
    public TimeSpan NextDrip;
    public bool HexacrocinDependent;
    public int WithdrawalCycle;
    public TimeSpan NextWithdrawal;
    public readonly HashSet<string> Overdoses = new();
    public TimeSpan PregnancyStarted;
    public TimeSpan LastClimax;
    public TimeSpan? HeatStarted;
    public bool Pregnant;
    public int PregnancyStage;
    public BodyFeature? OriginalBelly;
    public BodyFeature? PregnancyBelly;
}

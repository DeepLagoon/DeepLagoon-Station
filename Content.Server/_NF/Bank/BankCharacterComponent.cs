namespace Content.Server._NF.Bank;

/// <summary>The character owning this body's account, independent of lobby selection.</summary>
[RegisterComponent]
public sealed partial class BankCharacterComponent : Component
{
    [DataField] public Guid User;
    [DataField] public int Slot;
    [DataField] public int Profile;
}

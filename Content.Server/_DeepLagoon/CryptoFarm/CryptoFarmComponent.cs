namespace Content.Server._DeepLagoon.CryptoFarm;

[RegisterComponent]
public sealed partial class CryptoFarmComponent : Component
{
    /// <summary>Legacy physical coins remain recoverable; new mining pays the bound wallet.</summary>
    [DataField] public int Capacity = 15;

    [DataField] public Guid WalletUser;
    [DataField] public int WalletProfileId;
    [DataField] public string WalletCharacter = string.Empty;

    // The recipient is frozen when work is earned. These fields survive parking, including a lost DB reply.
    [DataField] public Guid MiningOperation;
    [DataField] public Guid MiningUser;
    [DataField] public int MiningProfileId;

    public bool IsBound => WalletUser != Guid.Empty && WalletProfileId > 0;
    public bool HasPendingMining => MiningOperation != Guid.Empty;
}

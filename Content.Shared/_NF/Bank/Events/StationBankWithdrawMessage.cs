using Robust.Shared.Serialization;

namespace Content.Shared._NF.Bank.Events;

/// <summary>
/// Raised on a client bank withdrawl
/// </summary>
[Serializable, NetSerializable]

public sealed class StationBankWithdrawMessage : BoundUserInterfaceMessage
{
    //amount to withdraw. validation is happening server side but we still need client input from a text field.
    public long Amount;
    public string? Reason;
    public string? Description;
    public StationBankWithdrawMessage(long amount, string? reason, string? description)
    {
        Amount = amount;
        Reason = reason;
        Description = description;
    }
}

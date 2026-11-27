using Content.Shared.CartridgeLoader;
using Content.Shared.Psionics;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Mantis;

[Serializable, NetSerializable]
public struct PsiRegistryEntry
{

    public uint Key;

    public string Name;

    public string Job;

    public PsionicsStatus Status;

    public string? Reason;

    public PsiRegistryEntry(uint key, string name, string job, PsionicsStatus status, string? reason)
    {
        Key = key;
        Name = name;
        Job = job;
        Status = status;
        Reason = reason;
    }
}

[Serializable, NetSerializable]
public sealed class PsiRegistryUiState : BoundUserInterfaceState
{
    public readonly List<PsiRegistryEntry> Records;

    public readonly uint? SelectedKey;

    public readonly bool CanEdit;

    public PsiRegistryUiState(List<PsiRegistryEntry> records, uint? selectedKey, bool canEdit)
    {
        Records = records;
        SelectedKey = selectedKey;
        CanEdit = canEdit;
    }
}

[Serializable, NetSerializable]
public sealed class PsiRegistrySelectMessage : CartridgeMessageEvent
{
    public readonly uint? Key;

    public PsiRegistrySelectMessage(uint? key)
    {
        Key = key;
    }
}

[Serializable, NetSerializable]
public sealed class PsiRegistryChangeStatusMessage : CartridgeMessageEvent
{
    public readonly PsionicsStatus Status;
    public readonly string? Reason;

    public PsiRegistryChangeStatusMessage(PsionicsStatus status, string? reason)
    {
        Status = status;
        Reason = reason;
    }
}

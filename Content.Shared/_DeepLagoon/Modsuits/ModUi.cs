using Content.Shared.Actions;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Modsuits;

[Serializable, NetSerializable]
public enum ModUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum ModOperation : byte
{
    ToggleModule,
    RemoveModule,
    ToggleSuit,
    ConfigureModule,
    Call,
    Accept,
    End,
    Rename,
    SetFrequency,
}

[Serializable, NetSerializable]
public sealed class ModUiMessage(ModOperation operation, NetEntity target = default, string text = "") : BoundUserInterfaceMessage
{
    public readonly ModOperation Operation = operation;
    public readonly NetEntity Target = target;
    public readonly string Text = text;
}

[Serializable, NetSerializable]
public sealed record ModModuleEntry(NetEntity Id, string Name, int Complexity, bool Active, bool Usable, bool RequiresPower, int? TargetTemperature);

[Serializable, NetSerializable]
public sealed record ModContact(NetEntity Id, string Name);

[Serializable, NetSerializable]
public enum ModCallState : byte
{
    Idle,
    Dialing,
    Incoming,
    Connected,
}

[Serializable, NetSerializable]
public sealed class ModUiState(
    int charge,
    bool control,
    bool active,
    bool busy,
    bool wearableControls,
    bool service,
    int capacity,
    int complexity,
    ModModuleEntry[] modules,
    string number,
    string label,
    int frequency,
    bool canSetFrequency,
    ModContact[] contacts,
    ModCallState call,
    string peer) : BoundUserInterfaceState
{
    public readonly int Charge = charge;
    public readonly bool Control = control;
    public readonly bool Active = active;
    public readonly bool Busy = busy;
    public readonly bool WearableControls = wearableControls;
    public readonly bool Service = service;
    public readonly int Capacity = capacity;
    public readonly int Complexity = complexity;
    public readonly ModModuleEntry[] Modules = modules;
    public readonly string Number = number;
    public readonly string Label = label;
    public readonly int Frequency = frequency;
    public readonly bool CanSetFrequency = canSetFrequency;
    public readonly ModContact[] Contacts = contacts;
    public readonly ModCallState Call = call;
    public readonly string Peer = peer;
}

public sealed partial class OpenModControlEvent : InstantActionEvent;

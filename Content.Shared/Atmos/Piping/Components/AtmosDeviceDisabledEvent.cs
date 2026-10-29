namespace Content.Shared.Atmos.Piping.Components;

/// <summary>
///     Raised directed on an atmos device when it leaves atmosphere processing.
/// </summary>
[ByRefEvent]
public readonly record struct AtmosDeviceDisabledEvent(bool PreserveSettings = false);

namespace Content.Server.Temperature;

/// <summary>
/// Allows active cooling to limit a temperature increase before it is applied.
/// </summary>
[ByRefEvent]
public record struct LimitTemperatureIncreaseEvent(float Temperature);

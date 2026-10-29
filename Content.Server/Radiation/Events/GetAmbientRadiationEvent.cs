namespace Content.Server.Radiation.Events;

/// <summary>Uniform environmental exposure, before container and personal radiation protection.</summary>
[ByRefEvent]
public record struct GetAmbientRadiationEvent(float Radiation = 0f);

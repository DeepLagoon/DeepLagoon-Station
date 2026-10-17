using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Performance;

[Serializable, NetSerializable]
public sealed class ServerFpsEvent(double framesPerSecond) : EntityEventArgs
{
    public double FramesPerSecond { get; } = framesPerSecond;
}

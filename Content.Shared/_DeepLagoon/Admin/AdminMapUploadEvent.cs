using System.Numerics;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Admin;

public static class AdminMapUploadLimits
{
    public const int MaxBytes = 20 * 1024 * 1024;
    public const int ChunkBytes = 32 * 1024;
    public const int TimeoutSeconds = 5 * 60;
}

[Serializable, NetSerializable]
public sealed class AdminMapUploadBeginEvent(Guid request, int length) : EntityEventArgs
{
    public readonly Guid Request = request;
    public readonly int Length = length;
}

[Serializable, NetSerializable]
public sealed class AdminMapUploadChunkEvent(Guid request, int offset, byte[] bytes) : EntityEventArgs
{
    public readonly Guid Request = request;
    public readonly int Offset = offset;
    public readonly byte[] Bytes = bytes;
}

[Serializable, NetSerializable]
public sealed class AdminMapUploadResultEvent(Guid request, string message) : EntityEventArgs
{
    public readonly Guid Request = request;
    public readonly string Message = message;
}

[Serializable, NetSerializable]
public sealed class AdminMapUploadProgressEvent(Guid request, int received, int total) : EntityEventArgs
{
    public readonly Guid Request = request;
    public readonly int Received = received;
    public readonly int Total = total;
}

[Serializable, NetSerializable]
public sealed class AdminMapPlacementReadyEvent(Guid request, NetEntity previewMap, NetEntity destinationMap,
    Box2 bounds, Vector2 position) : EntityEventArgs
{
    public readonly Guid Request = request;
    public readonly NetEntity PreviewMap = previewMap;
    public readonly NetEntity DestinationMap = destinationMap;
    public readonly Box2 Bounds = bounds;
    public readonly Vector2 Position = position;
}

[Serializable, NetSerializable]
public sealed class AdminMapPlacementConfirmEvent(Guid request, Vector2 position, float rotation) : EntityEventArgs
{
    public readonly Guid Request = request;
    public readonly Vector2 Position = position;
    public readonly float Rotation = rotation;
}

[Serializable, NetSerializable]
public sealed class AdminMapPlacementCancelEvent(Guid request) : EntityEventArgs
{
    public readonly Guid Request = request;
}

public static class AdminMapPlacementMath
{
    public static Vector2 Position(Vector2 original, Vector2 center, Vector2 destination, Angle rotation) =>
        destination + rotation.RotateVec(original - center);

    public static bool IsValid(Vector2 position, float rotation) =>
        float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(rotation) &&
        MathF.Abs(position.X) <= 1_000_000 && MathF.Abs(position.Y) <= 1_000_000;
}

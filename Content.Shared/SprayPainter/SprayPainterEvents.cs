using Content.Shared.DoAfter;
using Robust.Shared.Serialization;
using Content.Shared.Decals;
using Robust.Shared.Map;

namespace Content.Shared.SprayPainter;

[Serializable, NetSerializable]
public enum SprayPainterMode : byte
{
    Airlocks,
    Pipes,
    Decals,
}

[Serializable, NetSerializable]
public sealed class SprayPainterCustomColorMessage(Color color) : BoundUserInterfaceMessage
{
    public readonly Color Color = color;
}

[Serializable, NetSerializable]
public sealed class SprayPainterDecalSettingsMessage(Decal decal, bool snap) : BoundUserInterfaceMessage
{
    public readonly Decal Decal = decal;
    public readonly bool Snap = snap;
}

[Serializable, NetSerializable]
public sealed class SprayPainterPlaceDecalMessage(NetCoordinates coordinates, Decal decal) : BoundUserInterfaceMessage
{
    public readonly NetCoordinates Coordinates = coordinates;
    public readonly Decal Decal = decal;
}

[Serializable, NetSerializable]
public sealed class SprayPainterEraseDecalMessage(NetCoordinates coordinates) : BoundUserInterfaceMessage
{
    public readonly NetCoordinates Coordinates = coordinates;
}

[Serializable, NetSerializable]
public enum SprayPainterUiKey
{
    Key,
}

[Serializable, NetSerializable]
public sealed class SprayPainterSpritePickedMessage : BoundUserInterfaceMessage
{
    public readonly int Index;

    public SprayPainterSpritePickedMessage(int index)
    {
        Index = index;
    }
}

[Serializable, NetSerializable]
public sealed class SprayPainterColorPickedMessage : BoundUserInterfaceMessage
{
    public readonly string? Key;

    public SprayPainterColorPickedMessage(string? key)
    {
        Key = key;
    }
}

[Serializable, NetSerializable]
public sealed partial class SprayPainterDoorDoAfterEvent : DoAfterEvent
{
    /// <summary>
    /// Base RSI path to set for the door sprite.
    /// </summary>
    [DataField]
    public string Sprite;

    /// <summary>
    /// Department id to set for the door, if the style has one.
    /// </summary>
    [DataField]
    public string? Department;

    [DataField]
    public Color Color = Color.White;

    public SprayPainterDoorDoAfterEvent(string sprite, string? department, Color color)
    {
        Sprite = sprite;
        Department = department;
        Color = color;
    }

    public override DoAfterEvent Clone() => this;
}

[Serializable, NetSerializable]
public sealed partial class SprayPainterPipeDoAfterEvent : DoAfterEvent
{
    /// <summary>
    /// Color of the pipe to set.
    /// </summary>
    [DataField]
    public Color Color;

    public SprayPainterPipeDoAfterEvent(Color color)
    {
        Color = color;
    }

    public override DoAfterEvent Clone() => this;
}

using Content.Shared.DoAfter;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;

namespace Content.Shared.SprayPainter.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(raiseAfterAutoHandleState: true)]
public sealed partial class SprayPainterComponent : Component
{
    [DataField, AutoNetworkedField]
    public SprayPainterMode Mode;

    [DataField, AutoNetworkedField]
    public Color AirlockColor = Color.White;

    [DataField, AutoNetworkedField]
    public Color PipeColor = Color.White;

    [DataField, AutoNetworkedField]
    public Color DecalColor = Color.White;

    [DataField, AutoNetworkedField]
    public string DecalId = "";

    [DataField, AutoNetworkedField]
    public float DecalRotation;

    [DataField, AutoNetworkedField]
    public bool DecalSnap = true;

    [DataField, AutoNetworkedField]
    public int DecalZIndex;

    public Color SelectedColor => Mode switch
    {
        SprayPainterMode.Airlocks => AirlockColor,
        SprayPainterMode.Pipes => PipeColor,
        _ => DecalColor,
    };

    [DataField]
    public SoundSpecifier SpraySound = new SoundPathSpecifier("/Audio/Effects/spray2.ogg");

    [DataField]
    public TimeSpan AirlockSprayTime = TimeSpan.FromSeconds(3);

    [DataField]
    public TimeSpan PipeSprayTime = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Pipe color chosen to spray with.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string? PickedColor;

    /// <summary>
    /// Pipe colors that can be selected.
    /// </summary>
    [DataField]
    public Dictionary<string, Color> ColorPalette = new();

    /// <summary>
    /// Airlock style index selected.
    /// After prototype reload this might not be the same style but it will never be out of bounds.
    /// </summary>
    [DataField, AutoNetworkedField]
    public int Index;
}

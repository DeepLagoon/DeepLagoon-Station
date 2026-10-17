using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared.Audio;

/// <summary>Optional display metadata for a file in the lobby music collection.</summary>
[Prototype]
public sealed partial class LobbyMusicPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public ResPath Path;

    [DataField]
    public string? Name;

    [DataField]
    public string? Artist;

    /// <summary>Packaged texture; omitted or unavailable covers use the gray placeholder.</summary>
    [DataField]
    public ResPath? Cover;
}

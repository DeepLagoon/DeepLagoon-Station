using Robust.Shared.GameStates;

namespace Content.Shared.DetailExaminable;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class DetailExaminableComponent : Component
{
    [DataField(required: true), AutoNetworkedField]
    public string Content = string.Empty;
    [DataField, AutoNetworkedField] public string OocNotes = string.Empty;
    [DataField, AutoNetworkedField] public string CustomSpeciesName = string.Empty;
    [DataField, AutoNetworkedField] public bool CharacterCard;
    [DataField, AutoNetworkedField] public byte Erp;
    [DataField, AutoNetworkedField] public byte NonCon;
    [DataField, AutoNetworkedField] public byte Vore;
    // The storage identifier stays on the server. Clients request by examined entity.
    [DataField] public string HeadshotId = string.Empty;
    [DataField] public List<string> HeadshotImages = new();
}

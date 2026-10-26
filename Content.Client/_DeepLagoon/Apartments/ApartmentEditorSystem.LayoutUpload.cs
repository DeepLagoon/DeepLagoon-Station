using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Apartments;

namespace Content.Client._DeepLagoon.Apartments;

public sealed partial class ApartmentEditorSystem
{
    private readonly List<ApartmentPlacement> _uploadLayout = new();
    private Guid _uploadId, _uploadEditor;
    private string _uploadOperation = "";
    private int _uploadNext, _uploadCount;
    private TimeSpan _uploadStarted;

    private void ReceiveLayoutChunk(string payload)
    {
        if (_state == null || !TguiActionData.TryParse(payload, out var fields) ||
            !Guid.TryParse(fields!.String("upload"), out var upload) || upload == Guid.Empty ||
            fields.String("operation") is not ("preview" or "apply") ||
            !fields.TryInt("index", out var index) || !fields.TryInt("count", out var count) ||
            count is < 1 or > ApartmentLayout.SafetyLimit || index < 0 || index >= count ||
            !TryParseLayout(payload, out var part) || part.Count == 0) return;
        var operation = fields.String("operation")!;
        if (index == 0)
        {
            _uploadLayout.Clear(); _uploadId = upload; _uploadEditor = _state.Token;
            _uploadOperation = operation; _uploadNext = 0; _uploadCount = count; _uploadStarted = _timing.RealTime;
        }
        if (_uploadId != upload || _uploadEditor != _state.Token || _uploadNext != index || _uploadCount != count ||
            _uploadOperation != operation || _timing.RealTime - _uploadStarted > TimeSpan.FromSeconds(15) ||
            _uploadLayout.Count + part.Count > ApartmentLayout.SafetyLimit)
        { _uploadLayout.Clear(); _uploadId = Guid.Empty; return; }
        _uploadLayout.AddRange(part); _uploadNext++;
        if (_uploadNext != count) return;
        var completed = new TguiData().Array("layout", _uploadLayout.Select(PlacementData)).ToString();
        _uploadLayout.Clear(); _uploadId = Guid.Empty;
        OnAction(operation, completed);
    }
}

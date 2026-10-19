using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Mapping;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;

namespace Content.Client._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{
    private int _previewGeneration, _previewCount, _previewChunks, _previewNext, _previewUploadGeneration;
    private string? _previewId;
    private GridEditorExpansion? _previewExpansion;
    private readonly List<GridEditorItem> _preview = new();
    private List<GridEditorPeerDraft> _peerDrafts = new();

    private void PeerDrafts(GridEditorDraftsEvent ev)
    {
        if (_state == null || _state.Token != ev.Token) return;
        _peerDrafts = ev.Peers;
        PublishPeerDrafts();
    }

    private void PublishPeerDrafts()
    {
        _window?.Panel.Web.Send("update", new TguiData().Object("data", new TguiData()
            .Array("peerDrafts", _peerDrafts.Select(peer => new TguiData().String("player", peer.Player).String("name", peer.Name)
                .Object("expansion", new TguiData().Number("left", peer.Expansion.Left).Number("right", peer.Expansion.Right)
                    .Number("bottom", peer.Expansion.Bottom).Number("top", peer.Expansion.Top))
                .Array("items", peer.Items.Select(ItemData)).Array("removedItems", peer.RemovedItems.Select(ItemData))))
            .Array("peerVisuals", _peerDrafts.SelectMany(peer => peer.Items.Concat(peer.RemovedItems))
                .Select(item => (item.Kind, item.Prototype)).Distinct().Select(item => Visual(item.Kind, item.Prototype))))
            .ToString());
    }

    private bool PreviewAction(string action, MappingDataNode root)
    {
        if (action == "previewStart")
        {
            var id = Field(root, "id");
            if (id.Length is < 1 or > 64 || !int.TryParse(Field(root, "count"), out var count) || count < 0 ||
                !int.TryParse(Field(root, "chunks"), out var chunks) || chunks != Math.Max(1, (count + 15L) / 16) ||
                !int.TryParse(Field(root, "generation"), out _previewUploadGeneration)) return true;
            _previewExpansion = null;
            if (root.TryGet<MappingDataNode>("expansion", out var expansion))
            {
                if (!int.TryParse(Field(expansion, "left"), out var left) || !int.TryParse(Field(expansion, "right"), out var right) ||
                    !int.TryParse(Field(expansion, "bottom"), out var bottom) || !int.TryParse(Field(expansion, "top"), out var top)) return true;
                _previewExpansion = new GridEditorExpansion { Left = left, Right = right, Bottom = bottom, Top = top };
            }
            _previewId = id; _previewCount = count; _previewChunks = chunks; _previewNext = 0; _preview.Clear();
            AckPreview(); return true;
        }
        if (action != "previewChunk") return false;
        if (_state == null || _previewId == null || Field(root, "id") != _previewId ||
            !int.TryParse(Field(root, "index"), out var index) || index != _previewNext ||
            !root.TryGet<SequenceDataNode>("items", out var sequence) || sequence.Count > 16) return true;
        _preview.AddRange(ReadItems(sequence)); _previewNext++;
        if (_preview.Count > _previewCount) { _previewId = null; _preview.Clear(); return true; }
        if (_previewNext < _previewChunks) { AckPreview(); return true; }
        if (_preview.Count != _previewCount) { _previewId = null; _preview.Clear(); return true; }
        RaiseNetworkEvent(new GridEditorDraftUpdateEvent(_state.Token, _previewUploadGeneration, new List<GridEditorItem>(_preview), _previewExpansion));
        _window?.Panel.Web.Send("update", new TguiData().Object("data", new TguiData().String("previewFinished", _previewId)).ToString());
        _previewId = null; _preview.Clear(); return true;
    }

    private void AckPreview() => _window?.Panel.Web.Send("update", new TguiData().Object("data", new TguiData()
        .String("previewId", _previewId).Number("previewNext", _previewNext)).ToString());
}

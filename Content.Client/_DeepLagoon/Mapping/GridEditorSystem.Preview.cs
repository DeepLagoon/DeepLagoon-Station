using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Mapping;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;

namespace Content.Client._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{

    private void PeerDrafts(GridEditorDraftsEvent ev)
    {
        if (_tab.State == null || _tab.State.Token != ev.Token) return;
        _tab.PeerDrafts = ev.Peers;
        PublishPeerDrafts();
    }

    private void PublishPeerDrafts()
    {
        SendTabUpdate(new TguiData().Object("data", new TguiData()
            .Array("peerDrafts", _tab.PeerDrafts.Select(peer => new TguiData().String("player", peer.Player).String("name", peer.Name)
                .Object("expansion", new TguiData().Number("left", peer.Expansion.Left).Number("right", peer.Expansion.Right)
                    .Number("bottom", peer.Expansion.Bottom).Number("top", peer.Expansion.Top))
                .Array("items", peer.Items.Select(ItemData)).Array("removedItems", peer.RemovedItems.Select(ItemData))))
            .Array("peerVisuals", _tab.PeerDrafts.SelectMany(peer => peer.Items.Concat(peer.RemovedItems))
                .Select(item => (item.Kind, item.Prototype)).Distinct().Select(item => Visual(item.Kind, item.Prototype))))
            .ToString());
    }

    private bool PreviewAction(string action, MappingDataNode root)
    {
        if (action == "previewStart")
        {
            var id = Field(root, "id");
            var encoded = Field(root, "encoding") == "json";
            if (id.Length is < 1 or > 64 || !int.TryParse(Field(root, "count"), out var count) || count < 0 ||
                !int.TryParse(Field(root, "chunks"), out var chunks) || (encoded ? chunks is < 1 or > 32768 : chunks != Math.Max(1, (count + 15L) / 16)) ||
                !int.TryParse(Field(root, "generation"), out _tab.PreviewUploadGeneration)) return true;
            _tab.PreviewExpansion = null;
            if (root.TryGet<MappingDataNode>("expansion", out var expansion))
            {
                if (!int.TryParse(Field(expansion, "left"), out var left) || !int.TryParse(Field(expansion, "right"), out var right) ||
                    !int.TryParse(Field(expansion, "bottom"), out var bottom) || !int.TryParse(Field(expansion, "top"), out var top)) return true;
                _tab.PreviewExpansion = new GridEditorExpansion { Left = left, Right = right, Bottom = bottom, Top = top };
            }
            _tab.PreviewId = id; _tab.PreviewCount = count; _tab.PreviewChunks = chunks; _tab.PreviewNext = 0; _tab.Preview.Clear();
            _tab.PreviewText = encoded ? new System.Text.StringBuilder() : null;
            AckPreview(); return true;
        }
        if (action != "previewChunk") return false;
        if (_tab.State == null || _tab.PreviewId == null || Field(root, "id") != _tab.PreviewId ||
            !int.TryParse(Field(root, "index"), out var index) || index != _tab.PreviewNext) return true;
        if (_tab.PreviewText is { } text)
        {
            var part = Field(root, "text");
            if (part.Length is < 1 or > 1024 || text.Length + part.Length > 32 * 1024 * 1024) { _tab.PreviewId = null; _tab.PreviewText = null; return true; }
            text.Append(part);
            if (_tab.PreviewNext + 1 == _tab.PreviewChunks) _tab.Preview.AddRange(ReadItems(ReadItemText(text)));
        }
        else
        {
            if (!root.TryGet<SequenceDataNode>("items", out var sequence) || sequence.Count > 16) return true;
            _tab.Preview.AddRange(ReadItems(sequence));
        }
        _tab.PreviewNext++;
        if (_tab.Preview.Count > _tab.PreviewCount) { _tab.PreviewId = null; _tab.Preview.Clear(); return true; }
        if (_tab.PreviewNext < _tab.PreviewChunks) { AckPreview(); return true; }
        if (_tab.Preview.Count != _tab.PreviewCount) { _tab.PreviewId = null; _tab.Preview.Clear(); return true; }
        if (_tab.PreviewUploadGeneration == _tab.PreviewGeneration) RememberPreview();
        RaiseNetworkEvent(new GridEditorDraftUpdateEvent(_tab.State.Token, _tab.PreviewUploadGeneration, new List<GridEditorItem>(_tab.Preview), _tab.PreviewExpansion));
        SendTabUpdate(new TguiData().Object("data", new TguiData().String("previewFinished", _tab.PreviewId)).ToString());
        _tab.PreviewId = null; _tab.Preview.Clear(); _tab.PreviewText = null; return true;
    }

    private void AckPreview() => SendTabUpdate(new TguiData().Object("data", new TguiData()
        .String("previewId", _tab.PreviewId).Number("previewNext", _tab.PreviewNext)).ToString());
}

using System.IO;
using System.Linq;
using System.Text;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Mapping;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using YamlDotNet.RepresentationModel;

namespace Content.Client._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{
    private sealed class EditorTab
    {
        public long OpenOrder;
        public GridEditorStateEvent? State;
        public SnapshotTransfer? Snapshot;
        public string SearchQuery = "";
        public string SearchCategory = "tile";
        public int SearchPage;
        public string? UploadId;
        public int UploadCount;
        public int UploadChunks;
        public int UploadNext;
        public List<GridEditorItem> Upload = new();
        public StringBuilder? UploadText;
        public GridEditorExpansion? UploadExpansion;
        public GridEditorEnvironmentState? EnvironmentState;
        public int PatchVersion;
        public List<(int Version, GridEditorPatchEvent Patch)> PendingPatches = new();
        public int PreviewGeneration;
        public int PreviewCount;
        public int PreviewChunks;
        public int PreviewNext;
        public int PreviewUploadGeneration;
        public string? PreviewId;
        public GridEditorExpansion? PreviewExpansion;
        public List<GridEditorItem> Preview = new();
        public StringBuilder? PreviewText;
        public List<GridEditorPeerDraft> PeerDrafts = new();
        public readonly Dictionary<string, GridEditorItem> RecoveryEdits = new();
        public GridEditorExpansion RecoveryExpansion = new();
        public int LastAppliedVersion;
        public Stream? Download;
        public Guid DownloadRequest;
        public int DownloadIndex;
        public int DownloadCount;
        public int DraftFileLength;
        public int DraftFileChunks;
        public int DraftFileNext;
        public int DraftUploadNext;
        public HashSet<int> DownloadReceived = new();
        public bool DownloadBusy;
        public string? DraftFileId;
        public StringBuilder? DraftFile;
        public string? DraftUpload;
        public string UploadPurpose = "apply";
        public string UploadName = "";
        public bool ReferenceImporting;
    }

    private readonly Dictionary<Guid, EditorTab> _tabs = new();
    private EditorTab _tab = new();
    private Guid _activeTab;
    private long _nextTabOrder;
    private readonly Queue<Guid> _snapshotQueue = new();
    private const int SnapshotChunkItems = 1024;
    private sealed class SnapshotTransfer(TguiData header, List<GridEditorItem> items, string recovery)
    {
        public readonly string Id = Guid.NewGuid().ToString();
        public readonly TguiData Header = header;
        public readonly List<GridEditorItem> Items = items;
        public readonly string Recovery = recovery;
        public int RecoveryOffset;
        public int RecoveryChunks;
        public int Offset;
        public int Chunks;
        public bool Started;
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);
        if (_window is not { VisibleInTree: true } || !_window.Panel.Web.IsReady) return;
        while (_snapshotQueue.TryPeek(out var token))
        {
            if (!_tabs.TryGetValue(token, out var tab) || tab.Snapshot == null) { _snapshotQueue.Dequeue(); continue; }
            InTab(token, PublishSnapshotChunk);
            if (tab.Snapshot == null) _snapshotQueue.Dequeue();
            break;
        }
    }

    private void PublishSnapshotChunk()
    {
        if (_tab.Snapshot is not { } transfer) return;
        var message = new TguiData().String("id", transfer.Id);
        if (!transfer.Started)
        {
            transfer.Started = true;
            SendTabUpdate(new TguiData().Object("gridEditorSnapshot", message.String("phase", "start")).Object("data", transfer.Header).ToString());
        }
        else if (transfer.Offset < transfer.Items.Count)
        {
            var chunk = transfer.Items.Skip(transfer.Offset).Take(SnapshotChunkItems);
            SendTabUpdate(new TguiData().Object("gridEditorSnapshot", message.String("phase", "chunk").Number("index", transfer.Chunks++))
                .Object("data", new TguiData().Array("items", builder => GridEditorJson.AppendItems(builder, chunk))).ToString());
            transfer.Offset += Math.Min(SnapshotChunkItems, transfer.Items.Count - transfer.Offset);
        }
        else if (transfer.RecoveryOffset < transfer.Recovery.Length)
        {
            var length = Math.Min(32768, transfer.Recovery.Length - transfer.RecoveryOffset);
            var end = transfer.RecoveryOffset + length;
            if (end < transfer.Recovery.Length && transfer.Recovery[end - 1] is >= '\uD800' and <= '\uDBFF') length--;
            SendTabUpdate(new TguiData().Object("gridEditorSnapshot", message.String("phase", "draft").Number("index", transfer.RecoveryChunks++))
                .Object("data", new TguiData().String("text", transfer.Recovery.Substring(transfer.RecoveryOffset, length))).ToString());
            transfer.RecoveryOffset += length;
        }
        else
        {
            _tab.Snapshot = null;
            SendTabUpdate(new TguiData().Object("gridEditorSnapshot", message.String("phase", "complete").Number("chunks", transfer.Chunks)
                .Number("draftChunks", transfer.RecoveryChunks)).ToString());
            PublishPendingPatches(); PublishPeerDrafts();
            if (_tab.EnvironmentState is { } environment) EnvironmentState(environment);
            Search(_tab.SearchQuery, _tab.SearchCategory, _tab.SearchPage);
            PublishLibrary();
        }
    }

    private void InTab(Guid token, Action action)
    {
        if (!_tabs.TryGetValue(token, out var tab)) return;
        var previous = _tab;
        _tab = tab;
        try { action(); }
        finally { _tab = _tabs.Values.Contains(previous) ? previous : _tabs.GetValueOrDefault(_activeTab) ?? new EditorTab(); }
    }

    private void SendTabUpdate(string payload)
    {
        if (_tab.State == null) return;
        _window?.Panel.Web.Send("update", "{\"gridEditorToken\":" + GameWebView.Quote(_tab.State.Token.ToString()) + "," + payload[1..]);
    }

    private void ActivateTab(Guid token)
    {
        if (!_tabs.ContainsKey(token)) return;
        _activeTab = token;
        _tab = _tabs[token];
        _editingText = false;
        _window?.Panel.Web.Send("update", new TguiData().Object("data", new TguiData().String("activeEditorTab", token.ToString())
            .Array("editorTabOrder", builder =>
            {
                var separator = false;
                foreach (var id in _tabs.OrderBy(pair => pair.Value.OpenOrder).Select(pair => pair.Key))
                {
                    if (separator) builder.Append(',');
                    separator = true;
                    GameWebView.AppendQuoted(builder, id.ToString());
                }
            })).ToString());
    }

    private void TabAction(string action, string payload)
    {
        try
        {
            if (payload.Length > 4_000_000) return;
            var yaml = new YamlStream(); yaml.Load(new StringReader(payload));
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode.ToDataNode() is not MappingDataNode root ||
                !Guid.TryParse(Field(root, "tabToken"), out var token)) return;
            if (action == "activateTab") { ActivateTab(token); return; }
            InTab(token, () => Action(action, payload));
        }
        catch (Exception e) { Log.Warning($"Invalid grid editor tab action: {e.Message}"); }
    }

    private void CloseTab(Guid token, bool notifyServer)
    {
        if (!_tabs.ContainsKey(token)) return;
        InTab(token, CancelDownload);
        _tabs.Remove(token);
        if (notifyServer) RaiseNetworkEvent(new GridEditorCloseEvent(token));
        _window?.Panel.Web.Send("update", new TguiData().String("gridEditorClosed", token.ToString()).ToString());
        if (_tabs.Count == 0) { Close(); return; }
        if (_activeTab == token) ActivateTab(_tabs.MaxBy(pair => pair.Value.OpenOrder).Key);
    }
}

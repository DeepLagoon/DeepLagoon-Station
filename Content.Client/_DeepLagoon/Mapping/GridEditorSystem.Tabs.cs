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
        public GridEditorStateEvent? State;
        public string SearchQuery = "";
        public string SearchCategory = "tile";
        public int SearchPage;
        public string? UploadId;
        public int UploadCount;
        public int UploadChunks;
        public int UploadNext;
        public List<GridEditorItem> Upload = new();
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
        public List<GridEditorPeerDraft> PeerDrafts = new();
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
        _window?.Panel.Web.Send("update", new TguiData().Object("data", new TguiData().String("activeEditorTab", token.ToString())).ToString());
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
        if (_activeTab == token) ActivateTab(_tabs.Keys.Last());
    }
}

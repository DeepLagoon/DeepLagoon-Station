using System.IO;
using System.Text;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Mapping;
using Robust.Client.UserInterface;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Client._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{
    [Dependency] private IFileDialogManager _dialogs = default!;

    // Freeze the browser's geometry, then ask the server to preserve native component state.
    private bool DraftFileAction(string action, MappingDataNode root)
    {
        if (action is not ("draftFileStart" or "draftFileChunk")) return false;
        static string Field(MappingDataNode node, string key) => node.TryGet<ValueDataNode>(key, out var value) ? value.Value : "";
        var id = Field(root, "id");
        try
        {
            if (action == "draftFileStart")
            {
                if (_tab.State == null) throw new InvalidOperationException("Сессия редактора закрыта. Откройте grid заново.");
                if (_tab.DownloadBusy) throw new InvalidOperationException("Дождитесь завершения предыдущего сохранения.");
                if (id.Length is < 1 or > 64 || !int.TryParse(Field(root, "length"), out var length) || length is < 1 or > 32 * 1024 * 1024 ||
                    !int.TryParse(Field(root, "chunks"), out var chunks) || chunks != (length + 1023) / 1024)
                    throw new InvalidDataException("Некорректный размер черновика (максимум 32 МиБ текста).");
                _tab.DraftFileId = id; _tab.DraftFileLength = length; _tab.DraftFileChunks = chunks; _tab.DraftFileNext = 0;
                _tab.DraftFile = new StringBuilder(); _tab.DownloadRequest = Guid.NewGuid(); _tab.DownloadBusy = true;
                DownloadState(true);
            }
            else
            {
                if (_tab.DraftFile == null || id != _tab.DraftFileId) return true;
                if (!int.TryParse(Field(root, "index"), out var index) || index != _tab.DraftFileNext)
                    throw new InvalidDataException("Нарушен порядок частей черновика.");
                var text = Field(root, "text");
                if (text.Length != Math.Min(1024, _tab.DraftFileLength - _tab.DraftFile.Length))
                    throw new InvalidDataException("Неполная часть черновика.");
                _tab.DraftFile.Append(text); _tab.DraftFileNext++;
                if (_tab.DraftFileNext == _tab.DraftFileChunks)
                {
                    if (_tab.DraftFile.Length != _tab.DraftFileLength) throw new InvalidDataException("Неполный файл черновика.");
                    if (_tab.State == null) throw new InvalidOperationException("Сессия редактора закрыта.");
                    var plan = _tab.DraftFile.ToString(); _tab.DraftUpload = plan; _tab.DraftUploadNext = 0;
                    _tab.DraftFile = null;
                    _tab.Download = new MemoryStream(); _tab.DownloadIndex = 0; _tab.DownloadCount = 0; _tab.DownloadReceived.Clear();
                    RaiseNetworkEvent(new GridEditorDraftFileStart(_tab.State.Token, _tab.DownloadRequest, plan.Length));
                    return true;
                }
            }
            SendTabUpdate(new TguiData().Object("data", new TguiData()
                .String("draftFileId", id).Number("draftFileNext", _tab.DraftFileNext)).ToString());
        }
        catch (Exception e)
        {
            if (id == _tab.DraftFileId) CancelDownload();
            else FinishDraftFile(id);
            Status($"Не удалось сохранить черновик: {e.Message}", false);
        }
        return true;
    }

    private void FinishDraftFile(string id) => SendTabUpdate(new TguiData().Object("data",
        new TguiData().String("draftFileFinished", id)).ToString());

    private void ReceiveDraftFileAck(GridEditorDraftFileChunk ev)
    {
        // The server requests each next part with an empty chunk. Events within one tick can be reordered.
        if (_tab.DraftUpload == null || _tab.State?.Token != ev.Token || ev.Request != _tab.DownloadRequest ||
            ev.Text.Length != 0 || ev.Index != _tab.DraftUploadNext) return;
        var start = ev.Index * 32768;
        if (start < 0 || start >= _tab.DraftUpload.Length) return;
        _tab.DraftUploadNext++;
        RaiseNetworkEvent(new GridEditorDraftFileChunk(ev.Token, ev.Request, ev.Index,
            _tab.DraftUpload.Substring(start, Math.Min(32768, _tab.DraftUpload.Length - start))));
    }

    private void BeginDownload(bool wholeMap)
    {
        if (_tab.DownloadBusy || _tab.State == null) return;
        _tab.DownloadBusy = true;
        DownloadState(true);
        _tab.Download = new MemoryStream();
        _tab.DownloadRequest = Guid.NewGuid(); _tab.DownloadIndex = 0; _tab.DownloadCount = 0; _tab.DownloadReceived.Clear();
        RaiseNetworkEvent(new GridEditorDownloadEvent(_tab.State.Token, _tab.DownloadRequest, wholeMap));
    }

    private void ReceiveFile(GridEditorFileEvent ev)
    {
        if (_tab.Download == null || ev.Request != _tab.DownloadRequest) return;
        try
        {
            SendTabUpdate(new TguiData().Object("data", new TguiData()
                .Array("exportIssues", ev.Issue is { } issue ? new[] { new TguiData().String("id", issue.Id)
                    .String("prototype", issue.Prototype).String("name", issue.Name).String("component", issue.Component)
                    .String("message", issue.Message).Bool("located", issue.Located).Number("x", issue.X).Number("y", issue.Y) } : Array.Empty<TguiData>())).ToString());
            if (ev.Error.Length > 0) throw new IOException(ev.Error);
            if (ev.Count is < 1 or > 512 || ev.Index < 0 || ev.Index >= ev.Count || ev.Bytes.Length is < 1 or > 65536 ||
                ev.Index < ev.Count - 1 && ev.Bytes.Length != 65536 ||
                _tab.DownloadCount != 0 && _tab.DownloadCount != ev.Count) throw new InvalidDataException("Неполный файл карты.");
            _tab.DownloadCount = ev.Count;
            if (!_tab.DownloadReceived.Add(ev.Index)) return;
            _tab.Download.Position = (long) ev.Index * 65536;
            _tab.Download.Write(ev.Bytes); _tab.DownloadIndex++;
            if (_tab.DownloadIndex != _tab.DownloadCount) return;
            var bytes = ((MemoryStream) _tab.Download).ToArray();
            _tab.Download.Dispose(); _tab.Download = null;
            SaveDownloadedFile(bytes, _tab.DownloadRequest);
        }
        catch (Exception e) { CancelDownload(); Status($"Скачивание не завершено: {e.Message}", false); }
    }

    private async void SaveDownloadedFile(byte[] bytes, Guid request)
    {
        var tab = _tab;
        var token = tab.State!.Token;
        try
        {
            var file = await _dialogs.SaveFile(new FileDialogFilters(new FileDialogFilters.Group("yml", "yaml")));
            if (file == null) { InTab(token, CancelDownload); return; }
            using var stream = file.Value.fileStream;
            if (_window == null || !_tabs.ContainsKey(token) || request != tab.DownloadRequest) return;
            await stream.WriteAsync(bytes); await stream.FlushAsync();
            InTab(token, () => { CancelDownload(); Status("Файл .yml сохранён на вашем компьютере.", true); });
        }
        catch (Exception e) { InTab(token, () => { CancelDownload(); Status($"Не удалось сохранить файл: {e.Message}", false); }); }
    }

    private void DownloadState(bool busy) => SendTabUpdate(new TguiData().Object("data",
        new TguiData().Bool("downloading", busy)).ToString());
    private void CancelDownload()
    {
        _tab.Download?.Dispose(); _tab.Download = null; _tab.DownloadBusy = false;
        _tab.DownloadRequest = Guid.Empty;
        _tab.DraftFile = null; _tab.DraftUpload = null; _tab.DownloadReceived.Clear();
        if (_tab.DraftFileId != null) FinishDraftFile(_tab.DraftFileId);
        _tab.DraftFileId = null;
        DownloadState(false);
    }
}

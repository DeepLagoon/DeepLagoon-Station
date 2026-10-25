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
    private Stream? _download;
    private Guid _downloadRequest;
    private int _downloadIndex, _downloadCount;
    private readonly HashSet<int> _downloadReceived = new();
    private bool _downloadBusy;
    private string? _draftFileId;
    private StringBuilder? _draftFile;
    private int _draftFileLength, _draftFileChunks, _draftFileNext;
    private string? _draftUpload;
    private int _draftUploadNext;

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
                if (_state == null) throw new InvalidOperationException("Сессия редактора закрыта. Откройте grid заново.");
                if (_downloadBusy) throw new InvalidOperationException("Дождитесь завершения предыдущего сохранения.");
                if (id.Length is < 1 or > 64 || !int.TryParse(Field(root, "length"), out var length) || length is < 1 or > 32 * 1024 * 1024 ||
                    !int.TryParse(Field(root, "chunks"), out var chunks) || chunks != (length + 1023) / 1024)
                    throw new InvalidDataException("Некорректный размер черновика (максимум 32 МиБ текста).");
                _draftFileId = id; _draftFileLength = length; _draftFileChunks = chunks; _draftFileNext = 0;
                _draftFile = new StringBuilder(); _downloadRequest = Guid.NewGuid(); _downloadBusy = true;
                DownloadState(true);
            }
            else
            {
                if (_draftFile == null || id != _draftFileId) return true;
                if (!int.TryParse(Field(root, "index"), out var index) || index != _draftFileNext)
                    throw new InvalidDataException("Нарушен порядок частей черновика.");
                var text = Field(root, "text");
                if (text.Length != Math.Min(1024, _draftFileLength - _draftFile.Length))
                    throw new InvalidDataException("Неполная часть черновика.");
                _draftFile.Append(text); _draftFileNext++;
                if (_draftFileNext == _draftFileChunks)
                {
                    if (_draftFile.Length != _draftFileLength) throw new InvalidDataException("Неполный файл черновика.");
                    if (_state == null) throw new InvalidOperationException("Сессия редактора закрыта.");
                    var plan = _draftFile.ToString(); _draftUpload = plan; _draftUploadNext = 0;
                    _draftFile = null;
                    _download = new MemoryStream(); _downloadIndex = 0; _downloadCount = 0; _downloadReceived.Clear();
                    RaiseNetworkEvent(new GridEditorDraftFileStart(_state.Token, _downloadRequest, plan.Length));
                    return true;
                }
            }
            _window?.Panel.Web.Send("update", new TguiData().Object("data", new TguiData()
                .String("draftFileId", id).Number("draftFileNext", _draftFileNext)).ToString());
        }
        catch (Exception e)
        {
            if (id == _draftFileId) CancelDownload();
            else FinishDraftFile(id);
            Status($"Не удалось сохранить черновик: {e.Message}", false);
        }
        return true;
    }

    private void FinishDraftFile(string id) => _window?.Panel.Web.Send("update", new TguiData().Object("data",
        new TguiData().String("draftFileFinished", id)).ToString());

    private void ReceiveDraftFileAck(GridEditorDraftFileChunk ev)
    {
        // The server requests each next part with an empty chunk. Events within one tick can be reordered.
        if (_draftUpload == null || _state?.Token != ev.Token || ev.Request != _downloadRequest ||
            ev.Text.Length != 0 || ev.Index != _draftUploadNext) return;
        var start = ev.Index * 32768;
        if (start < 0 || start >= _draftUpload.Length) return;
        _draftUploadNext++;
        RaiseNetworkEvent(new GridEditorDraftFileChunk(ev.Token, ev.Request, ev.Index,
            _draftUpload.Substring(start, Math.Min(32768, _draftUpload.Length - start))));
    }

    private void BeginDownload(bool wholeMap)
    {
        if (_downloadBusy || _state == null) return;
        _downloadBusy = true;
        DownloadState(true);
        _download = new MemoryStream();
        _downloadRequest = Guid.NewGuid(); _downloadIndex = 0; _downloadCount = 0; _downloadReceived.Clear();
        RaiseNetworkEvent(new GridEditorDownloadEvent(_state.Token, _downloadRequest, wholeMap));
    }

    private void ReceiveFile(GridEditorFileEvent ev)
    {
        if (_download == null || ev.Request != _downloadRequest) return;
        try
        {
            if (ev.Error.Length > 0) throw new IOException(ev.Error);
            if (ev.Count is < 1 or > 512 || ev.Index < 0 || ev.Index >= ev.Count || ev.Bytes.Length is < 1 or > 65536 ||
                ev.Index < ev.Count - 1 && ev.Bytes.Length != 65536 ||
                _downloadCount != 0 && _downloadCount != ev.Count) throw new InvalidDataException("Неполный файл карты.");
            _downloadCount = ev.Count;
            if (!_downloadReceived.Add(ev.Index)) return;
            _download.Position = (long) ev.Index * 65536;
            _download.Write(ev.Bytes); _downloadIndex++;
            if (_downloadIndex != _downloadCount) return;
            var bytes = ((MemoryStream) _download).ToArray();
            _download.Dispose(); _download = null;
            SaveDownloadedFile(bytes, _downloadRequest);
        }
        catch (Exception e) { CancelDownload(); Status($"Скачивание не завершено: {e.Message}", false); }
    }

    private async void SaveDownloadedFile(byte[] bytes, Guid request)
    {
        try
        {
            // Open the destination only after the whole YAML has arrived, so a failed transfer cannot truncate it.
            var file = await _dialogs.SaveFile(new FileDialogFilters(new FileDialogFilters.Group("yml", "yaml")));
            if (file == null) { CancelDownload(); Status("Сохранение файла отменено.", true); return; }
            using var stream = file.Value.fileStream;
            if (_window == null || request != _downloadRequest) return;
            await stream.WriteAsync(bytes); await stream.FlushAsync();
            CancelDownload(); Status("Файл .yml сохранён на вашем компьютере.", true);
        }
        catch (Exception e) { CancelDownload(); Status($"Не удалось сохранить файл: {e.Message}", false); }
    }

    private void DownloadState(bool busy) => _window?.Panel.Web.Send("update", new TguiData().Object("data",
        new TguiData().Bool("downloading", busy)).ToString());
    private void CancelDownload()
    {
        _download?.Dispose(); _download = null; _downloadBusy = false;
        _downloadRequest = Guid.Empty;
        _draftFile = null; _draftUpload = null; _downloadReceived.Clear();
        if (_draftFileId != null) FinishDraftFile(_draftFileId);
        _draftFileId = null;
        DownloadState(false);
    }
}

using System.IO;
using Content.Client._DeepLagoon.WebUI;
using Robust.Client.UserInterface;

namespace Content.Client._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{
    private int _referenceVersion;
    private async void ImportReference()
    {
        if (_tab.ReferenceImporting || _window == null) return;
        _tab.ReferenceImporting = true;
        var window = _window;
        var tab = _tab;
        var token = tab.State!.Token;
        try
        {
            await using var file = await _dialogs.OpenFile(new FileDialogFilters(new FileDialogFilters.Group("png", "jpg", "jpeg", "webp")), FileAccess.Read);
            if (file == null) return;
            using var buffer = new MemoryStream(); var block = new byte[8192]; int size;
            while ((size = await file.ReadAsync(block)) > 0)
            {
                if (buffer.Length + size > 8 * 1024 * 1024) throw new InvalidDataException("Максимальный размер скриншота — 8 МБ.");
                buffer.Write(block, 0, size);
            }
            var bytes = buffer.ToArray();
            // ImageSharp's Identify/DetectFormat APIs are not available to sandboxed content.
            // The browser decodes the image and checks its dimensions before displaying it.
            var mime = ReferenceImageMime(bytes);
            if (mime == null) throw new InvalidDataException("Выберите PNG, JPEG или WebP.");
            if (_window != window) return;
            InTab(token, () => SendTabUpdate(new TguiData().Object("data", new TguiData()
                .String("referenceImage", $"data:{mime};base64,{Convert.ToBase64String(bytes)}")
                .Number("referenceVersion", ++_referenceVersion)).ToString()));
        }
        catch (Exception exception) { if (_window == window) InTab(token, () => Status($"Не удалось открыть скриншот: {exception.Message}", false)); }
        finally { tab.ReferenceImporting = false; }
    }

    private static string? ReferenceImageMime(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47 &&
            bytes[4] == 0x0d && bytes[5] == 0x0a && bytes[6] == 0x1a && bytes[7] == 0x0a)
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
            return "image/jpeg";
        if (bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
            return "image/webp";
        return null;
    }
}

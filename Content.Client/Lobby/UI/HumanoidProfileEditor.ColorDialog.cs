using System.Numerics;
using Content.Client._DeepLagoon.WebUI;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private CharacterAuxiliaryWindow? _colorWindow;
    private string _colorPickerId = "";
    private string _colorPickerTitle = "";
    private string _colorPickerValue = "#ffffff";
    private int _colorPickerSequence;
    private TguiData _colorPickerResult = new();

    private void CloseColorPicker()
    {
        var window = _colorWindow;
        _colorWindow = null;
        _colorPickerId = "";
        window?.Close();
        window?.Dispose();
    }

    private bool HandleColorPicker(string action, TguiActionData args)
    {
        var id = args.String("id") ?? "";
        if (action == "color-picker-close")
        {
            if (id != _colorPickerId) return false;
            CloseColorPicker();
            PublishTguiProfile();
            return true;
        }
        if (action == "color-picker-open")
        {
            if (id.Length is 0 or > 128 || !Color.TryFromHex(args.String("value"), out _)) return false;
            _colorPickerId = id;
            _colorPickerTitle = args.String("title") ?? "Цвет";
            _colorPickerValue = args.String("value") ?? "#ffffff";
            if (_colorWindow == null)
            {
                var window = new CharacterAuxiliaryWindow { SetSize = new Vector2(380, 430) };
                _colorWindow = window;
                window.Panel.OnAction += (nextAction, payload) =>
                {
                    if (TguiActionData.TryParse(payload, out var nextArgs)
                        && nextAction is "color-picker-change" or "color-picker-close")
                        HandleColorPicker(nextAction, nextArgs!);
                };
                window.Panel.OnClose += () => { CloseColorPicker(); PublishTguiProfile(); };
                window.OnClose += () =>
                {
                    if (_colorWindow != window) return;
                    _colorWindow = null;
                    _colorPickerId = "";
                    window.Dispose();
                    PublishTguiProfile();
                };
                window.OpenCentered();
            }
            _colorWindow.MoveToFront();
            PublishColorPicker();
            PublishTguiProfile();
            return true;
        }
        if (action != "color-picker-change" || _colorWindow == null || id != _colorPickerId
            || !Color.TryFromHex(args.String("value"), out var color)) return false;
        _colorPickerValue = color.ToHexNoAlpha();
        _colorPickerResult = new TguiData().String("id", id).String("value", _colorPickerValue)
            .Number("sequence", ++_colorPickerSequence);
        PublishColorPicker();
        PublishTguiProfile();
        return true;
    }

    private void PublishColorPicker() => _colorWindow?.Panel.SetState("CharacterColorPicker",
        new TguiData().String("id", _colorPickerId).String("title", _colorPickerTitle).String("value", _colorPickerValue)
            .Object("appearance", _editorAppearance.Data()).ToString());
}

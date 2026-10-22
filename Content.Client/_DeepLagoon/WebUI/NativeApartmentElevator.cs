using Robust.Client.UserInterface.Controls;

namespace Content.Client._DeepLagoon.WebUI;

internal sealed class NativeApartmentElevator : BoxContainer
{
    private readonly Action<string, string> _send;
    private NativeUiData _data = new();
    private string _number = "";

    public NativeApartmentElevator(Action<string, string> send)
    {
        _send = send;
        Orientation = LayoutOrientation.Vertical;
        SeparationOverride = 10;
        HorizontalExpand = true;
    }

    public void Update(NativeUiData data)
    {
        _data = data;
        Render();
    }

    private void EnterKey(string key)
    {
        if (_data.Bool("busy") || _data.Bool("atHome")) return;
        if (key == "own") _number = _data.String("number");
        else if (key == "clear") _number = "";
        else if (key == "backspace") _number = _number.Length > 0 ? _number[..^1] : "";
        else if (key.Length == 1 && key[0] is >= '0' and <= '9' && _number.Length < 8) _number += key;
        Render();
    }

    private void Render()
    {
        RemoveAllChildren();
        var busy = _data.Bool("busy");
        NativeInterfacePanel.Text(this, "ЛИФТ ЖИЛОГО КОМПЛЕКСА");
        NativeInterfacePanel.Text(this, "Ваш номер квартиры: " + _data.String("number") + " · До конца раунда");
        if (!_data.Bool("atHome"))
            NativeInterfacePanel.Button(this, "Ввести свой номер", () => EnterKey("own"), !busy);
        NativeInterfacePanel.Text(this, "Все находящиеся в кабине поедут вместе. Дождитесь открытия дверей.");
        if (_data.Bool("atHome"))
            NativeInterfacePanel.Button(this, "Вернуться на жилую остановку", () => _send("return", "{}"), !busy);
        else
        {
            NativeInterfacePanel.Text(this, "Номер квартиры");
            AddChild(new Label { Text = _number.Length == 0 ? "— — — — — — — —" : _number, Margin = new(8) });
            var keys = new[] { "7", "8", "9", "4", "5", "6", "1", "2", "3", "clear", "0", "backspace" };
            for (var row = 0; row < 4; row++)
            {
                var line = NativeInterfacePanel.Row();
                AddChild(line);
                for (var column = 0; column < 3; column++)
                {
                    var key = keys[row * 3 + column];
                    var caption = key == "clear" ? "C" : key == "backspace" ? "⌫" : key;
                    var button = NativeInterfacePanel.Button(line, caption, () => EnterKey(key), !busy && (key.Length > 1 || _number.Length < 8));
                    button.ToolTip = key == "clear" ? "Сбросить номер" : key == "backspace" ? "Удалить цифру" : key;
                    button.MinSize = new(72, 40);
                    button.HorizontalExpand = true;
                }
            }
            NativeInterfacePanel.Button(this, "Отправка", () => _send("depart", new TguiData().String("number", _number).ToString()), !busy && _number.Length == 8);
        }
        NativeInterfacePanel.Text(this, _data.String("message") is { Length: > 0 } message ? message : "Для поездки в гости достаточно знать номер квартиры.");
        NativeInterfacePanel.Text(this, "Рабочая одежда и вещи сдаются на хранение и возвращаются после обратной поездки.");
    }
}

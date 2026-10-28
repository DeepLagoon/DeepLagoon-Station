using System.Globalization;
using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private BoxContainer? _nativeBody;
    private NativeTattooEditor? _nativeTattoos;

    private void InitializeNativeExtras()
    {
        if (_nativeBody != null) return;
        _nativeBody = NativeInterfacePanel.Column();
        var bodyScroll = new ScrollContainer { HScrollEnabled = false };
        bodyScroll.AddChild(_nativeBody); TabContainer.AddChild(bodyScroll); TabContainer.SetTabTitle(bodyScroll, "Тело");
        _nativeTattoos = new NativeTattooEditor((action, payload) => HandleTguiProfileAction(action, payload));
        var tattooScroll = new ScrollContainer { HScrollEnabled = false };
        tattooScroll.AddChild(_nativeTattoos); TabContainer.AddChild(tattooScroll); TabContainer.SetTabTitle(tattooScroll, "Татуировки");
        var tools = PreviewColumn.GetChild(0);
        NativeInterfacePanel.Button(tools, "Профиль персонажа", () => HandleTguiProfileAction("character-card", "{}"));
        NativeInterfacePanel.Button(tools, "Изображения профиля", ToggleHeadshotLibrary);
        RefreshNativeExtras();
    }

    private void RefreshNativeExtras()
    {
        if (_nativeBody == null || _nativeTattoos == null || Profile == null) return;
        _nativeTattoos.Update(NativeUiData.Parse(_tattooEditor.Build(PreviewDummy, Profile.Appearance.Tattoos, _tattooStatus)
            .Bool("fileBusy", _tattooFileBusy).ToString()));
        _nativeBody.RemoveAllChildren();
        var features = NativeUiData.Parse(new TguiData().Array("features", BodyCustomizationData()).ToString());
        foreach (var part in features.Array("features"))
        {
            var column = NativeInterfacePanel.Column(); _nativeBody.AddChild(column);
            NativeInterfacePanel.Text(column, part.String("name"));
            void Send(string field, string value) => UserInterfaceManager.DeferAction(() =>
                HandleTguiProfileAction("body-feature", new TguiData().String("part", part.String("part")).String("field", field).String("value", value).ToString()));
            foreach (var (field, title) in new[] { ("enabled", "Включено"), ("showThroughClothing", "Деформировать одежду"), ("matchSkin", "Цвет кожи"), ("secondaryEnabled", "Второй цвет") })
            {
                if (field == "secondaryEnabled" && !part.Bool("secondaryAvailable")) continue;
                var check = new CheckBox { Text = title, Pressed = part.Bool(field) }; column.AddChild(check);
                check.OnToggled += args => Send(field, args.Pressed ? "true" : "false");
            }
            foreach (var (key, field) in new[] { ("shapes", "shape"), ("sizes", "size") })
                NativeInterfacePanel.Options(column, part.Array(key).Select(item => (item.String("id"), item.String("name"))), value => Send(field, value), part.String(field));
            foreach (var (field, title) in new[] { ("color", "Цвет #RRGGBB"), ("secondaryColor", "Второй цвет #RRGGBB") })
            {
                var input = NativeInterfacePanel.Field(column, title, part.String(field));
                NativeInterfacePanel.Button(column, "Применить цвет", () => Send(field, input.Text));
            }
            NativeInterfacePanel.Options(column, new[] { ("0", "Скрыто"), ("1", "Под одеждой"), ("2", "Поверх одежды") }, value => Send("visibility", value), part.String("visibility"));
            foreach (var (field, title) in new[] { ("womb", "Матка"), ("anus", "Анус") })
            {
                if (field == "womb" && part.String("part") != "vagina" || field == "anus" && part.String("part") != "butt") continue;
                var check = new CheckBox { Text = title, Pressed = part.Bool(field) }; column.AddChild(check);
                check.OnToggled += args => Send(field, args.Pressed ? "true" : "false");
            }
        }
    }
}

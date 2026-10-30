using System.Globalization;
using System.IO;
using System.Linq;
using Content.Shared._DeepLagoon.InteractionPanel;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Utility;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using YamlDotNet.RepresentationModel;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>
/// Native views for content interfaces that have no upstream window. They use the same
/// validated action handlers as TGUI. No browser, script execution or JSON editor is involved.
/// Existing upstream interfaces register their own typed window through SetNativeFallback.
/// </summary>
public sealed partial class NativeInterfacePanel : BoxContainer
{
    private readonly Action<string, string> _send;
    private readonly Action _close;
    private readonly BoxContainer _body = new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 6 };
    private string _interface = "";
    private bool _shopGallery;
    private NativeUiData _data = new();
    private NativeApartmentEditor? _apartment;
    private NativeGridEditor? _grid;
    private NativeArtEditor? _art;
    private NativeTattooEditor? _tattoo;
    private NativeApartmentElevator? _elevator;
    private readonly Dictionary<string, string> _interactionDraft = new();
    private int _interactionTab;
    private string _interactionSearch = "";

    public static bool Supports(string name) => name is "InteractionPanel" or "CharacterCard" or
        "CharacterHeadshotLibrary" or "CharacterSavePrompt" or "ShipyardPreview" or "ApartmentTravel" or "ApartmentTerminal" or
        "LagoonEconomy" or "ApartmentShop" or "ApartmentEditor" or "ApartmentElevator" or "GridEditor" or "ArtStudio" or "TattooStudio" or "DeepLagoonDemo";

    public NativeInterfacePanel(Action<string, string> send, Action close)
    {
        _send = send; _close = close;
        Orientation = LayoutOrientation.Vertical;
        HorizontalExpand = VerticalExpand = true;
        var scroll = new ScrollContainer { HorizontalExpand = true, VerticalExpand = true, HScrollEnabled = false };
        scroll.AddChild(_body);
        AddChild(scroll);
        var closeButton = new Button { Text = Loc.GetString("dl-ui-native-close") };
        closeButton.OnPressed += _ => RequestClose();
        AddChild(closeButton);
    }

    public void RequestClose()
    {
        if (CanClose()) _close();
    }
    public bool CanClose()
    {
        if (_apartment?.Dirty == true || _grid?.Dirty == true || _art?.Dirty == true || _tattoo?.Dirty == true)
        {
            Text(_body, Loc.GetString("dl-ui-native-unsaved"));
            return false;
        }
        return true;
    }

    public void UpdateState(string name, string json)
    {
        _data = NativeUiData.Parse(json);
        if (_interface == name)
        {
            if (name == "InteractionPanel" && _data.Bool("arousalOnly") && _interactionArousal != null)
            {
                UpdateInteractionArousal();
                return;
            }
            if (name == "InteractionPanel" && _data.Bool("gearOnly") && _interactionGear != null)
            {
                UpdateInteractionGear();
                return;
            }
            if (_elevator != null) { _elevator.Update(_data); return; }
            if (_apartment != null) { _apartment.Update(_data); return; }
            if (_grid != null) { _grid.Update(_data); return; }
            if (_art != null) { _art.Update(_data); return; }
            if (_tattoo != null) { _tattoo.Update(_data); return; }
        }
        _interface = name;
        _body.RemoveAllChildren();
        switch (name)
        {
            case "LagoonEconomy": Economy(); break;
            case "ApartmentElevator": _elevator = new NativeApartmentElevator(_send); _body.AddChild(_elevator); _elevator.Update(_data); break;
            case "ApartmentTravel": case "ApartmentTerminal": Travel(); break;
            case "CharacterCard": Card(); break;
            case "ShipyardPreview": Shipyard(); break;
            case "CharacterHeadshotLibrary": Gallery(); break;
            case "CharacterSavePrompt":
                Text(_body, "Сохранить изменения персонажа?");
                Button(_body, "Сохранить", () => Send("save"));
                Button(_body, "Не сохранять", () => Send("discard"));
                Button(_body, "Отмена", () => Send("cancel"));
                break;
            case "InteractionPanel": Interaction(); break;
            case "ApartmentShop":
                var shopTabs = Row(); _body.AddChild(shopTabs);
                Button(shopTabs, "Квартиры и предметы", () => { _shopGallery = false; UpdateState(name, json); });
                Button(shopTabs, "Галерея", () => { _shopGallery = true; Send("gallery", new TguiData().Number("page", 0)); });
                if (_shopGallery)
                {
                    ApartmentGallery();
                    break;
                }
                Text(_body, "Номер квартиры: " + _data.String("number"));
                if (long.TryParse(_data.String("balance"), out var shopBalance) && shopBalance >= 0) Text(_body, "Баланс: " + _data.String("balance") + " LC");
                Text(_body, _data.String("message"));
                foreach (var entry in _data.Array("templates"))
                {
                    Text(_body, entry.String("name") + " · " + entry.String("price") + " LC");
                    if (entry.Bool("selected")) { Text(_body, "Выбрано"); continue; }
                    var confirmation = Column(); confirmation.Visible = false;
                    Button(_body, entry.Bool("owned") ? "Выбрать" : "Купить и выбрать", () => confirmation.Visible = true);
                    Text(confirmation, entry.Bool("owned") ? "Выбрать этот шаблон?" : "Купить за " + entry.String("price") + " LC и выбрать?");
                    var confirm = Button(confirmation, "Подтвердить", () =>
                        Send("select", new TguiData().String("template", entry.String("id"))));
                    confirm.OnPressed += _ => confirm.Disabled = true;
                    Button(confirmation, "Отмена", () => confirmation.Visible = false);
                    _body.AddChild(confirmation);
                }
                Text(_body, "Предметы — доставка ко входу в квартиру");
                var pendingItem = _data.String("pendingItemOrder");
                if (pendingItem.Length > 0)
                    Button(_body, "Завершить доставку", () => Send("retryItem", new TguiData().String("order", pendingItem)));
                foreach (var item in _data.Array("items"))
                {
                    Text(_body, item.String("name") + " · " + item.String("category") + " · " + item.String("price") + " LC");
                    var confirmation = Column(); confirmation.Visible = false;
                    Button(_body, "Купить", () => confirmation.Visible = true, pendingItem.Length == 0);
                    Text(confirmation, "Купить за " + item.String("price") + " LC? Предмет появится у входа в квартиру.");
                    var order = Guid.NewGuid().ToString();
                    var confirm = Button(confirmation, "Подтвердить", () => Send("buyItem", new TguiData()
                        .String("template", item.String("id")).String("order", order).String("unitPrice", item.String("price"))));
                    confirm.OnPressed += _ => confirm.Disabled = true;
                    Button(confirmation, "Отмена", () => confirmation.Visible = false);
                    _body.AddChild(confirmation);
                }
                Button(_body, "Обустройство", () => Send("edit"));
                if (_data.Bool("legacy")) Button(_body, "Покинуть старую квартиру", () => Send("legacyExit"));
                break;
            case "ApartmentEditor": _apartment = new NativeApartmentEditor(_send); _body.AddChild(_apartment); _apartment.Update(_data); break;
            case "GridEditor": _grid = new NativeGridEditor(_send); _body.AddChild(_grid); _grid.Update(_data); break;
            case "ArtStudio": _art = new NativeArtEditor(_send); _body.AddChild(_art); _art.Update(_data); break;
            case "TattooStudio": _tattoo = new NativeTattooEditor(_send); _body.AddChild(_tattoo); _tattoo.Update(_data); break;
            case "DeepLagoonDemo":
                Text(_body, "Счётчик: " + _data.String("count"));
                Button(_body, "+1", () => Send("increment")); Button(_body, "Сбросить", () => Send("reset")); break;
            case "": break;
            default:
                // Developer previews can request arbitrary frontend interfaces, but production
                // hosts must register a typed fallback or a view in this catalogue.
                Text(_body, Loc.GetString("dl-ui-native-title") + ": " + name);
                break;
        }
    }

    public void Receive(string type, string json)
    {
        if (type != "update") return;
        var update = NativeUiData.Parse(json).Object("data");
        _grid?.Receive(update);
        _apartment?.Receive(update);
    }

    private void Send(string action, TguiData? fields = null) => _send(action, (fields ?? new TguiData()).ToString());
    internal static BoxContainer Column() => new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 4, HorizontalExpand = true };
    internal static BoxContainer Row() => new() { SeparationOverride = 4, HorizontalExpand = true };
    internal static void Text(Control parent, string text)
    {
        var label = new RichTextLabel(); label.SetMessage(FormattedMessage.FromUnformatted(text)); parent.AddChild(label);
    }
    internal static Button Button(Control parent, string text, Action pressed, bool enabled = true)
    {
        var button = new Button { Text = text, Disabled = !enabled };
        // Native actions can cause state replay and rebuild this tree.
        button.OnPressed += _ => parent.UserInterfaceManager.DeferAction(pressed);
        parent.AddChild(button); return button;
    }
    internal static LineEdit Field(Control parent, string title, string value = "")
    {
        Text(parent, title);
        var edit = new LineEdit { Text = value, HorizontalExpand = true }; parent.AddChild(edit); return edit;
    }
    internal static OptionButton Options(Control parent, IEnumerable<(string Id, string Name)> items, Action<string> selected, string? current = null)
    {
        var button = new OptionButton();
        var values = items.ToArray();
        for (var i = 0; i < values.Length; i++) button.AddItem(values[i].Name, i);
        var index = Array.FindIndex(values, value => value.Id == current);
        if (index >= 0) button.SelectId(index);
        button.OnItemSelected += args => { button.SelectId(args.Id); selected(values[args.Id].Id); };
        parent.AddChild(button); return button;
    }

    private void Travel()
    {
        var pending = _data.Bool("pending");
        Text(_body, _data.String("message"));
        if (pending) Text(_body, "Подготовка перехода… Не двигайтесь.");
        if (_data.Bool("atHome"))
        {
            Button(_body, "Вернуться на остановку", () => Send("return"), !pending);
            if (_data.Bool("canManage")) { Button(_body, "Обустройство квартиры", () => Send("edit"), !pending); Button(_body, "Шаблоны квартир", () => Send("shop"), !pending); }
        }
        else
        {
            Text(_body, "Снаряжение будет сдано на хранение и возвращено после поездки.");
            Button(_body, "Отправиться домой", () => Send("depart"), !pending && _data.Bool("canDepart"));
            foreach (var person in _data.Array("invitations"))
                Button(_body, "В гости: " + person.String("name"), () => Send("visit", new TguiData().String("target", person.String("user"))), !pending && _data.Bool("canDepart"));
        }
        if (_data.Bool("canManage"))
        {
            Button(_body, _data.Bool("entryOpen") ? "Закрыть вход" : "Открыть вход",
                () => Send(_data.Bool("entryOpen") ? "closeEntry" : "openEntry"), !pending);
            foreach (var person in _data.Array("people"))
            {
                var row = Row(); _body.AddChild(row);
                Text(row, person.String("name") + (person.Bool("inside") ? " · В квартире" : person.Bool("invited") ? " · " + person.String("seconds") + " сек." : ""));
                var target = new TguiData().String("target", person.String("user"));
                Button(row, person.Bool("invited") ? "Отозвать" : "Пригласить",
                    () => Send(person.Bool("invited") ? "revoke" : "invite", target), !pending && (_data.Bool("entryOpen") || person.Bool("invited")) && !person.Bool("inside"));
                if (person.Bool("inside"))
                {
                    var kick = Button(row, "Вернуть гостя", () => { }, !pending);
                    var confirmed = false;
                    kick.OnPressed += _ => { if (confirmed) Send("kick", target); else { confirmed = true; kick.Text = "Подтвердить возвращение"; } };
                }
            }
        }
        Button(_body, "Обновить список", () => Send("refresh"), !pending);
    }

    private void Card()
    {
        // The live engine SpriteView stays in the owning window.
        var column = Column(); column.Margin = new Thickness(280, 0, 0, 0); _body.AddChild(column);
        Text(column, _data.String("name"));
        var row = Row(); column.AddChild(row);
        Button(row, "◀", () => Send("char_left")); Button(row, "▶", () => Send("char_right"));
        Text(column, _data.String("flavor")); Text(column, _data.String("ooc"));
        NativeImage.Add(column, _data.String("headshot"));
        foreach (var image in _data.Array("headshots")) NativeImage.Add(column, image.String("image"));
        foreach (var key in new[] { "erp", "noncon", "vore" })
            Text(column, key.ToUpperInvariant() + ": " + (InteractionPanelConsent)_data.Int(key));
    }

    private void ApartmentGallery()
    {
        Text(_body, "Личная картина");
        Text(_body, "Купите рамку и долговечный холст в разделе «Предметы». Используйте рамку на стене своей квартиры, нарисуйте картину, закройте редактор и вставьте холст в рамку. Картина и слои сохранятся между раундами без публикации в библиотеке. Обычный холст не подходит.");
        var gallery = _data.Object("gallery");
        var page = gallery.Int("page");
        Button(_body, "Обновить галерею", () => Send("gallery", new TguiData().Number("page", page)));
        if (!gallery.Bool("loaded")) { Text(_body, "Загрузка картин квартиры…"); return; }
        if (gallery.String("error").Length > 0) { Text(_body, gallery.String("error")); return; }
        Text(_body, "Картин в квартире: " + gallery.Int("total"));
        foreach (var painting in gallery.Array("paintings"))
        {
            Text(_body, painting.String("title") + (painting.Bool("framed") ? " · В рамке" : " · Холст вне рамки"));
            NativeImage.Add(_body, painting.String("image"));
        }
        var pages = Row(); _body.AddChild(pages);
        Button(pages, "Предыдущие картины", () => Send("gallery", new TguiData().Number("page", page - 1)), page > 0);
        Button(pages, "Следующие картины", () => Send("gallery", new TguiData().Number("page", page + 1)),
            (page + 1) * Content.Shared._DeepLagoon.Apartments.ApartmentGalleryState.PageSize < gallery.Int("total"));
    }

    private void Gallery()
    {
        var gallery = _data.Object("gallery");
        Text(_body, _data.String("name")); Text(_body, gallery.String("error")); Text(_body, _data.String("headshotStatus"));
        foreach (var image in gallery.Array("images"))
        {
            var id = image.String("id");
            NativeImage.Add(_body, image.String("image"));
            var row = Row(); _body.AddChild(row);
            Button(row, image.Bool("primary") ? "Основное" : "Сделать основным", () => Send("headshot-gallery", new TguiData().String("operation", "primary").String("value", id)));
            if (gallery.Bool("library")) Button(row, image.Bool("active") ? "Скрыть" : "Показать", () => Send("headshot-gallery", new TguiData().String("operation", "toggle").String("value", id)));
            Button(row, "Заменить", () => Send("headshot-upload", new TguiData().String("value", id)));
            var remove = Button(row, "Удалить", () => { });
            var confirmed = false;
            remove.OnPressed += _ => { if (confirmed) Send("headshot-gallery", new TguiData().String("operation", "delete").String("value", id)); else { confirmed = true; remove.Text = "Подтвердить удаление"; } };
        }
        if (gallery.Array("images").Count() < gallery.Int("capacity")) Button(_body, "Добавить изображение", () => Send("headshot-add"));
    }

    private ProgressBar? _interactionArousal;
    private Label? _interactionArousalLabel;
    private BoxContainer? _interactionGear;

    private void UpdateInteractionGear()
    {
        if (_interactionGear == null) return;
        var box = _interactionGear;
        box.RemoveAllChildren();
        Text(box, Loc.GetString("dl-interaction-panel-gear-help"));
        void Gear(BoxContainer parent, string operation, string slot = "", string value = "", bool enabled = true) =>
            Button(parent, Loc.GetString("dl-interaction-panel-gear-" + operation),
                () => Send("gear", new TguiData().String("operation", operation).String("slot", slot).String("value", value)), enabled);
        Text(box, Loc.GetString("dl-interaction-panel-gear-held") + ": " + _data.String("heldName"));
        Gear(box, "attach", enabled: _data.String("heldSlot").Length > 0);
        if (_data.Bool("self")) Gear(box, "release-all");
        foreach (var item in _data.Array("gear"))
        {
            var slot = item.String("slot");
            Text(box, Loc.GetString("dl-lustwish-slot-" + slot) + ": " + item.String("name"));
            var row = Row(); box.AddChild(row);
            Gear(row, "remove", slot, enabled: !item.Bool("locked"));
            Gear(row, item.Bool("locked") ? "unlock" : "lock", slot);
            if (_data.Bool("self")) Gear(row, "escape", slot);
            if (slot.StartsWith("neck", StringComparison.Ordinal)) Gear(box, item.Bool("leashed") ? "unleash" : "leash", slot);
            if (item.Bool("electronic"))
            {
                Gear(box, "pair", slot); Gear(box, "mode", slot, (item.Int("intensity") % 3 + 1).ToString());
            }
            if (item.Bool("electronic") || item.Bool("hypnosis")) Gear(box, "toggle", slot, item.Bool("active") ? "0" : "1");
            if (item.Bool("shock")) Gear(box, "pulse", slot);
        }
    }

    private void UpdateInteractionArousal()
    {
        if (_interactionArousal == null || _interactionArousalLabel == null) return;
        var maximum = _data.Float("arousalMaximum");
        var fraction = maximum > 0 ? Math.Clamp(_data.Float("arousalCurrent") / maximum, 0, 1) : 0;
        _interactionArousal.Value = fraction;
        _interactionArousalLabel.Text = $"{Loc.GetString("dl-interaction-panel-arousal")} · {MathF.Round(fraction * 100)}%";
    }

    private void Interaction()
    {
        Text(_body, _data.String("name")); Text(_body, _data.String("status"));
        var meter = new PanelContainer { HorizontalExpand = true, MinHeight = 20 };
        _interactionArousal = new ProgressBar
        {
            MinValue = 0, MaxValue = 1, HorizontalExpand = true, MinHeight = 20,
            BackgroundStyleBoxOverride = new StyleBoxFlat(Color.FromHex("#261735")),
            ForegroundStyleBoxOverride = new StyleBoxFlat(Color.FromHex("#9A5BD2")),
        };
        _interactionArousalLabel = new Label { HorizontalAlignment = HAlignment.Center, VerticalAlignment = VAlignment.Center };
        meter.AddChild(_interactionArousal);
        meter.AddChild(_interactionArousalLabel);
        _body.AddChild(meter);
        UpdateInteractionArousal();
        var selectedTab = _interactionTab;
        var tabs = new TabContainer { HorizontalExpand = true }; _body.AddChild(tabs);
        tabs.OnTabChanged += tab => _interactionTab = tab;
        var actions = Column(); tabs.AddChild(actions); TabContainer.SetTabTitle(actions, "Действия");
        var interval = Field(actions, "Интервал повтора, сек.", _data.String("interval"));
        Button(actions, "Установить интервал", () => { if (float.TryParse(interval.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) Send("interval", new TguiData().Number("value", value)); });
        Button(actions, "Остановить повторы", () => Send("stop"));
        var search = Field(actions, "Поиск действий", _interactionSearch);
        var list = Column(); actions.AddChild(list);
        void Fill()
        {
            list.RemoveAllChildren();
            foreach (var action in _data.Array("actions").OrderByDescending(a => a.Bool("favorite")))
            {
                if (!action.String("name").Contains(search.Text, StringComparison.OrdinalIgnoreCase)) continue;
                var row = Row(); list.AddChild(row);
                var args = new TguiData().String("id", action.String("id"));
                Button(row, action.Bool("favorite") ? "★" : "☆", () => Send("favorite", args));
                Button(row, action.String("name"), () => Send("run", args), action.Bool("available"));
                Button(row, action.Bool("repeating") ? "Стоп" : "Повтор", () => Send("repeat", args), action.Bool("available"));
            }
        }
        search.OnTextChanged += _ => { _interactionSearch = search.Text; Fill(); }; Fill();
        var body = Column(); tabs.AddChild(body); TabContainer.SetTabTitle(body, "Тело");
        foreach (var part in _data.Array("bodyFeatures"))
        {
            Text(body, part.String("name"));
            var row = Row(); body.AddChild(row);
            foreach (var (value, title) in new[] { (0, "Скрыто"), (1, "Под одеждой"), (2, "Поверх одежды") })
                Button(row, title, () => Send("body-visibility", new TguiData().String("id", part.String("part")).Number("value", value)));
        }
        var custom = Column(); tabs.AddChild(custom); TabContainer.SetTabTitle(custom, "Свои действия");
        Text(custom, _data.String("libraryStatus"));
        var titleEdit = Field(custom, "Название", _interactionDraft.GetValueOrDefault("title", ""));
        var template = Field(custom, "Шаблон сообщения", _interactionDraft.GetValueOrDefault("template", ""));
        var color = Field(custom, "Цвет (#RRGGBB)", _interactionDraft.GetValueOrDefault("color", "#C6E8DA"));
        titleEdit.OnTextChanged += _ => _interactionDraft["title"] = titleEdit.Text;
        template.OnTextChanged += _ => _interactionDraft["template"] = template.Text;
        color.OnTextChanged += _ => _interactionDraft["color"] = color.Text;
        var id = _interactionDraft.GetValueOrDefault("id", "");
        var region = _interactionDraft.GetValueOrDefault("body", "all");
        var sound = _interactionDraft.GetValueOrDefault("sound", "");
        int.TryParse(_interactionDraft.GetValueOrDefault("category", "3"), out var category);
        var regions = _data.Array("regions").ToArray(); var categories = _data.Array("categories").ToArray();
        var sounds = new[] { ("", "Без звука") }.Concat(_data.Array("sounds").Select(r => (r.String("id"), r.String("name")))).ToArray();
        var regionOptions = Options(custom, regions.Select(r => (r.String("id"), r.String("name"))), selected => { region = selected; _interactionDraft["body"] = selected; }, region);
        var categoryOptions = Options(custom, categories.Select(r => (r.String("id"), r.String("name"))), selected => { int.TryParse(selected, out category); _interactionDraft["category"] = selected; }, category.ToString(CultureInfo.InvariantCulture));
        var soundOptions = Options(custom, sounds, selected => { sound = selected; _interactionDraft["sound"] = selected; }, sound);
        var flags = new List<(CheckBox Box, int Flag)>();
        foreach (var flag in Enum.GetValues<InteractionPanelActionFlags>())
        {
            var value = (int)flag;
            if (value == 0 || (value & (value - 1)) != 0) continue;
            var title = value switch { 1 => "На себе", 2 => "На других", 4 => "Гуманоиды", _ => "Животные" };
            var check = new CheckBox { Text = title, Pressed = _interactionDraft.GetValueOrDefault("flag" + value, value is 2 or 4 ? "true" : "false") == "true" };
            check.OnToggled += args => _interactionDraft["flag" + value] = args.Pressed ? "true" : "false";
            custom.AddChild(check); flags.Add((check, value));
        }
        Button(custom, "Новое действие", () => { _interactionDraft.Clear(); _body.RemoveAllChildren(); Interaction(); });
        Button(custom, "Сохранить", () => Send("save", new TguiData().String("id", id).String("title", titleEdit.Text)
            .String("template", template.Text).String("body", region).String("sound", sound).String("color", color.Text)
            .Number("category", category).Number("flags", flags.Where(f => f.Box.Pressed).Aggregate(0, (value, f) => value | f.Flag))), _data.Bool("libraryLoaded") && !_data.Bool("waiting"));
        foreach (var action in _data.Array("custom"))
        {
            var row = Row(); custom.AddChild(row);
            Button(row, "Изменить: " + action.String("title"), () =>
            {
                id = action.String("id"); titleEdit.Text = action.String("title"); template.Text = action.String("template");
                region = action.String("body"); sound = action.String("sound"); color.Text = action.String("color"); category = action.Int("category");
                _interactionDraft["id"] = id; _interactionDraft["title"] = titleEdit.Text; _interactionDraft["template"] = template.Text;
                _interactionDraft["body"] = region; _interactionDraft["sound"] = sound; _interactionDraft["color"] = color.Text;
                _interactionDraft["category"] = category.ToString(CultureInfo.InvariantCulture);
                var regionIndex = Array.FindIndex(regions, entry => entry.String("id") == region);
                var categoryIndex = Array.FindIndex(categories, entry => entry.Int("id") == category);
                var soundIndex = Array.FindIndex(sounds, entry => entry.Item1 == sound);
                if (regionIndex >= 0) regionOptions.SelectId(regionIndex);
                if (categoryIndex >= 0) categoryOptions.SelectId(categoryIndex);
                if (soundIndex >= 0) soundOptions.SelectId(soundIndex);
                foreach (var f in flags)
                {
                    f.Box.Pressed = (action.Int("flags") & f.Flag) != 0;
                    _interactionDraft["flag" + f.Flag] = f.Box.Pressed ? "true" : "false";
                }
            });
            var remove = Button(row, "Удалить", () => { }); var confirmed = false;
            remove.OnPressed += _ => { if (confirmed) Send("delete", new TguiData().String("id", action.String("id"))); else { confirmed = true; remove.Text = "Подтвердить удаление"; } };
        }
        _interactionGear = Column(); tabs.AddChild(_interactionGear);
        TabContainer.SetTabTitle(_interactionGear, Loc.GetString("dl-interaction-panel-tab-gear"));
        UpdateInteractionGear();
        tabs.CurrentTab = Math.Clamp(selectedTab, 0, 3);
    }
}

/// <summary>Bounded sandbox-compatible state reader shared by native views.</summary>
public sealed class NativeUiData
{
    private readonly MappingDataNode _node;
    public NativeUiData() : this(new MappingDataNode()) { }
    public NativeUiData(MappingDataNode node) => _node = node;
    public static NativeUiData Parse(string json)
    {
        if (json.Length > 16 * 1024 * 1024) return new();
        try { using var reader = new StringReader(json); var stream = new YamlStream(); stream.Load(reader); return stream.Documents.Count == 1 && stream.Documents[0].RootNode.ToDataNode() is MappingDataNode map ? new(map) : new(); }
        catch (Exception) { return new(); }
    }
    public string String(string key, string fallback = "") => _node.TryGet<ValueDataNode>(key, out var value) ? value.Value : fallback;
    public bool Bool(string key) => String(key) == "true";
    public int Int(string key) => int.TryParse(String(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    public float Float(string key) => float.TryParse(String(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value) ? value : 0;
    public NativeUiData Object(string key) => _node.TryGet<MappingDataNode>(key, out var map) ? new(map) : new();
    public IEnumerable<NativeUiData> Array(string key) => _node.TryGet<SequenceDataNode>(key, out var sequence) ? sequence.OfType<MappingDataNode>().Select(map => new NativeUiData(map)) : [];
}

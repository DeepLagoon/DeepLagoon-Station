using System.Linq;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._DeepLagoon.WebUI;

public sealed partial class NativeInterfacePanel
{
    private int _parkingTab;
    private string _parkingDock = "";
    private void Parking()
    {
        var data = _data;
        var usable = data.Bool("id") && !data.Bool("busy");
        void Act(string action, TguiData? fields = null) => Send(action, fields);
        void Rebuild() { _body.RemoveAllChildren(); Parking(); }
        Text(_body, "Пользователь: " + data.String("user"));
        Button(_body, data.Bool("id") ? "Извлечь ID-карту" : "Вставить ID-карту", () => Act("id"), !data.Bool("busy"));
        Text(_body, "Кредиты: " + data.String("credits") + " кр. · Lagoon Coin: " + data.String("coins") + " LC");
        // Separate rows keep every Russian caption visible at large UI scales.
        Button(_body, "Парковка", () => { _parkingTab = 0; Rebuild(); });
        Button(_body, "Долгосрочное хранение", () => { _parkingTab = 1; Rebuild(); });
        Button(_body, "Купить места", () => { _parkingTab = 2; Rebuild(); });
        if (_parkingTab == 0)
        {
            Text(_body, "Шаттл: " + data.String("shipName"));
            Text(_body, "Категория: " + data.String("category") + " · Владелец: " + data.String("owner"));
            Text(_body, data.Bool("parked") ? "На парковке · заморожен" : "Парковка бесплатна");
            DockSelector(data);
            Button(_body, data.Bool("parked") ? "Вернуть бесплатно" : "Поставить на парковку",
                () => Act(data.Bool("parked") ? "recall" : "park", new TguiData().String("dock", _parkingDock)), usable && data.Bool("hasShip"));
            Button(_body, "Сохранить надолго…", () => { _parkingTab = 1; Rebuild(); }, usable && data.Bool("hasShip"));
            Text(_body, "Бесплатная парковка действует до конца смены. Для переноса шаттла в новую смену сохраните его в долгосрочное место.");
        }
        else if (_parkingTab == 1)
        {
            Text(_body, data.String("description"));
            DockSelector(data);
            foreach (var place in data.Array("places"))
            {
                Text(_body, place.String("categoryName") + " · " + (place.String("name").Length == 0 ? "Свободно" : place.String("name")));
                TguiData Fields() => new TguiData().String("slot", place.String("id")).String("dock", _parkingDock);
                if (place.String("name").Length == 0)
                    Button(_body, "Сохранить текущий шаттл…", () => Act("preview", Fields()), usable && data.Bool("hasShip") && data.Int("shipCategory") <= place.Int("category"));
                else
                {
                    Text(_body, "Категория шаттла: " + place.String("shipCategory") + " · Владелец: " + place.String("owner"));
                    Text(_body, "Выкуп: " + place.String("price") + " кр.");
                    if (place.Bool("paid")) Text(_body, "Оплата уже учтена. Повторного списания не будет.");
                    ConfirmParking(place.Bool("paid") ? "Получить оплаченный шаттл…" : "Выкупить…", place.Bool("paid") ? "Завершить оплаченный выкуп?" : "Выкупить за " + place.String("price") + " кр.?", () => Act("redeem", Fields()), usable && !data.Bool("hasShip"));
                }
            }
        }
        else
        {
            Text(_body, "В каждом месте хранится один шаттл своей или меньшей категории.");
            foreach (var category in data.Array("categories"))
            {
                var count = category.Int("count");
                var price = category.String(count > 0 ? "extra" : "first");
                Text(_body, category.String("name") + " · куплено мест: " + count);
                Text(_body, "Первое: " + category.String("first") + " LC · следующее: " + category.String("extra") + " LC");
                var token = data.String("buyToken");
                ConfirmParking((count > 0 ? "Ещё место" : "Купить место") + " · " + price + " LC", "Оплатить " + price + " LC?",
                    () => Act("buy", new TguiData().Number("category", category.Int("id")).String("token", token)), usable);
            }
        }
        if (data.String("reviewToken").Length > 0)
        {
            Text(_body, "Проверка перед сохранением. Следующие добавленные предметы останутся у консоли:");
            foreach (var excluded in data.Array("excluded")) Text(_body, "• " + excluded.String("name"));
            Text(_body, "Штатное оборудование сохранится. Шаттл будет убран из мира.");
            var confirm = new CheckBox { Text = "Я проверил список" }; _body.AddChild(confirm);
            var save = Button(_body, "Сохранить шаттл", () => Act("save", new TguiData().String("slot", data.String("reviewSlot")).String("token", data.String("reviewToken"))), false);
            confirm.OnToggled += args => save.Disabled = !args.Pressed || !usable;
            Button(_body, "Отмена", () => Act("cancel"));
        }
        Text(_body, data.String("message"));
        Button(_body, "Обновить", () => Act("refresh"), !data.Bool("busy"));
    }
    private void DockSelector(NativeUiData data)
    {
        var docks = data.Array("docks").ToArray();
        if (!docks.Any(d => d.String("id") == _parkingDock)) _parkingDock = docks.FirstOrDefault()?.String("id") ?? "";
        Text(_body, "Шлюз для возврата");
        Options(_body, docks.Select(d => (d.String("id"), d.String("name"))), selected => _parkingDock = selected, _parkingDock);
    }
    private void ConfirmParking(string label, string question, Action action, bool enabled)
    {
        var panel = Column(); panel.Visible = false;
        Button(_body, label, () => panel.Visible = true, enabled);
        Text(panel, question);
        var confirm = Button(panel, "Подтвердить", action, enabled);
        confirm.OnPressed += _ => confirm.Disabled = true;
        Button(panel, "Отмена", () => panel.Visible = false);
        _body.AddChild(panel);
    }
}

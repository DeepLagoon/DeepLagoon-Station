using System.Linq;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client._DeepLagoon.WebUI;

public sealed partial class NativeInterfacePanel
{
    private int _parkingTab;
    private string _parkingDock = "";
    private string _parkingRecallDock = "";
    private readonly NativeParkingDockMap.View _parkingMapView = new();
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
        if (!data.Bool("repairEnabled"))
        {
            if (_parkingTab == 3) _parkingTab = 0;
            CloseParkingRepairConfirmation();
            _parkingRepairSelected.Clear();
        }
        if (data.Bool("repairEnabled"))
            Button(_body, "Ремонт шаттла", () => { _parkingTab = 3; Rebuild(); });
        if (_parkingTab == 0)
        {
            Text(_body, "Шаттл: " + data.String("shipName"));
            Text(_body, "Категория: " + data.String("category") + " · Владелец: " + data.String("owner"));
            Text(_body, data.Bool("parked") ? "На парковке · заморожен" : "Парковка бесплатна");
            if (data.Bool("parked") && data.String("parkingSnapshot").Length > 0)
                Text(_body, "Защита от краша: " + data.String("parkingSnapshot"));
            if (data.Bool("recoveredParking"))
            {
                DockSelector(data);
                Button(_body, "Вернуть бесплатно", () => Act("recall", new TguiData().String("dock", _parkingDock)), usable && _parkingDock.Length > 0);
            }
            else if (data.Bool("parked"))
                RecallDockSelector(data.Object("recallMap"), usable && data.Bool("hasShip"));
            else
                Button(_body, "Поставить на парковку", () => Act("park"), usable && data.Bool("hasShip"));
            Button(_body, "Сохранить надолго…", () => { _parkingTab = 1; Rebuild(); }, usable && data.Bool("hasShip") && !data.Bool("recoveredParking"));
            if (!data.Bool("recoveredParking")) Text(_body, "В конце смены шаттл на бесплатной парковке продадут в вашу пользу. Для переноса в новую смену сохраните его в долгосрочное место.");
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
                    var freeReturn = place.Int("price") == 0;
                    var waiting = place.Int("returnWaitSeconds") > 0;
                    if (waiting && long.TryParse(place.String("returnReadyAtMs"), out var readyAtMs) && readyAtMs > 0)
                        _body.AddChild(new ParkingReturnCountdown(readyAtMs));
                    else
                        Text(_body, waiting ? "Бесплатный возврат пока недоступен."
                            : freeReturn ? "Возврат бесплатный" : "Выкуп: " + place.String("price") + " кр.");
                    if (place.Bool("paid")) Text(_body, "Оплата уже учтена. Повторного списания не будет.");
                    var label = place.Bool("paid") ? "Получить оплаченный шаттл…" : freeReturn ? "Вернуть бесплатно…" : "Выкупить…";
                    var question = place.Bool("paid") ? "Завершить оплаченный выкуп?"
                        : freeReturn ? "Вернуть шаттл бесплатно?" : "Выкупить за " + place.String("price") + " кр.?";
                    ConfirmParking(label, question, () => Act("redeem", Fields()), usable && !data.Bool("hasShip") && !waiting && _parkingDock.Length > 0);
                }
            }
        }
        else if (_parkingTab == 3)
        {
            ParkingRepair(data.Object("repair"), usable);
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
            Text(_body, "Следующие предметы исключены из сохранения. Запрещённые будут проданы в вашу пользу, остальные останутся у консоли.");
            foreach (var excluded in data.Array("excluded")) Text(_body, "• " + excluded.String("name"));
            Text(_body, "Разрешённые предметы, которые не удастся сохранить, останутся у консоли. После сохранения шаттл будет убран из мира.");
            var confirm = new CheckBox { Text = "Я проверил список" }; _body.AddChild(confirm);
            var save = Button(_body, "Сохранить шаттл", () => Act("save", new TguiData().String("slot", data.String("reviewSlot")).String("token", data.String("reviewToken"))), false);
            confirm.OnToggled += args => save.Disabled = !args.Pressed || !usable;
            Button(_body, "Отмена", () => Act("cancel"));
        }
        Text(_body, data.String("message"));
        Button(_body, "Обновить", () => Act("refresh"), !data.Bool("busy"));
    }

    private sealed class ParkingReturnCountdown : RichTextLabel
    {
        private readonly long _readyAtMs;
        private int _seconds = -1;

        public ParkingReturnCountdown(long readyAtMs)
        {
            _readyAtMs = readyAtMs;
            UpdateCountdown(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        protected override void FrameUpdate(FrameEventArgs args)
        {
            base.FrameUpdate(args);
            if (_seconds != 0)
                UpdateCountdown(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        private void UpdateCountdown(long nowMs)
        {
            var seconds = (int)Math.Ceiling(Math.Max(0, _readyAtMs - nowMs) / 1000d);
            if (seconds == _seconds)
                return;

            _seconds = seconds;
            SetMessage(FormattedMessage.FromUnformatted($"Бесплатный возврат через {seconds / 60}:{seconds % 60:00}"));
        }
    }
    private void DockSelector(NativeUiData data)
    {
        var docks = data.Array("docks").ToArray();
        if (!docks.Any(d => d.String("id") == _parkingDock)) _parkingDock = docks.FirstOrDefault()?.String("id") ?? "";
        Text(_body, "Шлюз для возврата");
        Options(_body, docks.Select(d => (d.String("id"), d.String("name"))), selected => _parkingDock = selected, _parkingDock);
    }

    private void RecallDockSelector(NativeUiData data, bool usable)
    {
        var ports = data.Array("ports").ToArray();
        if (ports.Length == 0)
        {
            Text(_body, "На карте станции нет доступных для выбора портов.");
            return;
        }

        if (!ports.Any(port => port.String("id") == _parkingRecallDock))
            _parkingRecallDock = "";

        Text(_body, "Колёсико меняет масштаб, перетаскивание перемещает схему, клик выбирает порт.");
        var map = new NativeParkingDockMap(data, _parkingMapView);
        _body.AddChild(map);
        Button(_body, "Вся станция", map.ResetView);
        var status = new RichTextLabel { HorizontalExpand = true };
        var recall = new Button { Text = "Вернуть бесплатно", Disabled = true };
        recall.OnPressed += _ => Send("recall", new TguiData().String("dock", _parkingRecallDock));
        var options = Options(_body, new[] { ("", "Выберите стыковочный порт") }.Concat(
            ports.Select(port => (port.String("id"), port.String("name") + " · " + ParkingPortLabel(port.String("status"))))),
            Select, _parkingRecallDock);
        options.Disabled = !usable;
        _body.AddChild(status);
        _body.AddChild(recall);
        if (usable)
            map.OnSelect += id =>
            {
                options.SelectId(Array.FindIndex(ports, port => port.String("id") == id) + 1);
                Select(id);
            };
        Select(_parkingRecallDock);

        void Select(string id)
        {
            _parkingRecallDock = id;
            map.Selected = id;
            var selected = ports.FirstOrDefault(port => port.String("id") == id)?.String("status") ?? "";
            status.SetMessage(ParkingPortLabel(selected));
            recall.Disabled = !usable || selected != "fits";
        }
    }

    private static string ParkingPortLabel(string status) => status switch
    {
        "fits" => "Шаттл помещается",
        "blocked" => "Шаттл не помещается",
        "occupied" => "Порт занят",
        _ => "Зелёный — подходит, красный — не подходит, жёлтый — занят.",
    };
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

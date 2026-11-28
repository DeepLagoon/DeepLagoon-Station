using System.Globalization;
using System.Linq;
using Content.Shared._DeepLagoon.Performance;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._DeepLagoon.Diagnostics;

public sealed partial class PerformanceMonitorEui
{
    private static string Date(double seconds) => seconds is >= 0 and < 253402300799
        ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).ToOffset(TimeSpan.FromHours(3)).ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture) : "—";
    private static string Duration(double seconds) => seconds >= 86400 ? $"{seconds / 86400:0.0} д" : seconds >= 3600 ? $"{seconds / 3600:0.0} ч" : seconds >= 60 ? $"{seconds / 60:0.0} мин" : $"{seconds:0} с";

    private BoxContainer HistoryToolbar()
    {
        var body = Column(3);
        var row = Row();
        row.AddChild(new Label { Text = "Интервал" });
        _rangeChoice = new OptionButton { HorizontalExpand = true };
        foreach (var text in new[] { "30 секунд", "1 минута", "2 минуты", "10 минут", "30 минут", "1 час", "6 часов", "24 часа", "7 дней", "Весь раунд" }) _rangeChoice.AddItem(text);
        _rangeChoice.SelectId(2);
        _rangeChoice.OnItemSelected += e =>
        {
            _rangeChoice.SelectId(e.Id); _seconds = Ranges[e.Id];
            SendMessage(new PerformanceHistoryRequest(_seconds));
        };
        var live = new Button { Text = "В эфир" };
        live.OnPressed += _ => SendMessage(new PerformanceHistoryRequest(_seconds));
        row.AddChild(_rangeChoice); row.AddChild(live);
        body.AddChild(row);
        _journalStatus = Text("Загрузка журнала раунда…");
        body.AddChild(_journalStatus);
        return body;
    }

    private static PanelContainer Section(string title, Control contents, string colour)
    {
        var body = Column(6); body.Margin = new Thickness(10);
        if (title.Length > 0) body.AddChild(new Label { Text = title, FontColorOverride = Color.FromHex("#82D8D2") });
        body.AddChild(contents);
        return new PanelContainer { HorizontalExpand = true, PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex(colour) }, Children = { body } };
    }

    private static Label Value(string text, Color? colour = null) => new()
        { Text = text, ClipText = true, ToolTip = text, HorizontalExpand = true, MinWidth = 0, FontColorOverride = colour };

    private void RenderSystems()
    {
        if (_lastState is not { } data) return;
        _systemRows.DisposeAllChildren();
        var source = data.Systems.Where(row => row.Name.Contains(_systemFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        source = _systemOrder.SelectedId switch
        {
            1 => source.OrderByDescending(row => row.MaximumMs),
            2 => source.OrderByDescending(row => row.AllocatedBytes ?? -1),
            3 => source.OrderByDescending(row => row.FailedCalls ?? -1),
            _ => source.OrderByDescending(row => row.AverageMs),
        };
        var rank = 0;
        foreach (var system in source.Take(32))
        {
            var body = Column(4);
            var header = Row();
            header.AddChild(new Label { Text = (++rank).ToString("00"), FontColorOverride = Color.FromHex("#8293A6") });
            header.AddChild(Value(system.Name, Color.FromHex("#E4EDF4")));
            body.AddChild(header);
            var times = Row();
            times.AddChild(Value("Среднее " + Number(system.AverageMs) + " мс/тик", Color.FromHex("#79D9D1")));
            times.AddChild(Value("Пик вызова " + Number(system.MaximumMs) + " мс", Color.FromHex("#F3BE7D")));
            times.AddChild(Value("Пик за тик " + Number(system.MaximumTickMs) + " мс"));
            body.AddChild(times);
            var counts = Row();
            counts.AddChild(Value("Вызовов " + (system.Calls >= 0 ? system.Calls.ToString("N0") : "N/A")));
            counts.AddChild(Value("Аллокации " + (system.AllocatedBytes is { } bytes ? Number(bytes / 1048576d) + " МиБ" : "N/A")));
            counts.AddChild(Value("Ошибок " + (system.FailedCalls?.ToString("N0") ?? "N/A")));
            body.AddChild(counts);
            _systemRows.AddChild(Section("", body, rank % 2 == 0 ? "#1B2733" : "#202F3B"));
        }
        if (rank == 0) _systemRows.AddChild(Text(data.Systems.Length == 0
            ? "Нет свежего рейтинга систем." : "Системы по этому запросу не найдены."));
    }

    private void RenderPvs(PerformanceMonitorState data)
    {
        _pvsRows.DisposeAllChildren(); _contextRows.DisposeAllChildren();
        var metrics = data.TickMetrics.ToDictionary(row => row.Name, row => row.Value);
        _pvsRows.AddChild(Text("Поток тика · среднее / максимум · мс"));
        foreach (var (key, title) in new[]
        {
            ("pvs_sessions", "Подготовка сессий"), ("pvs_wait_previous", "Ожидание прошлых workers"),
            ("pvs_prepare", "Подготовка снимка"), ("pvs_visibility_acks", "Видимость и ACK"),
            ("pvs_serialize", "Сериализация"), ("pvs_send", "Отправка"),
            ("pvs_cleanup", "Очистка"), ("pvs_tick_maintenance", "Обслуживание тика"), ("pvs_dispatch", "Dispatch · включительно"),
        })
        {
            var row = Row(); row.AddChild(Value(title));
            row.AddChild(new Label { Text = Number(metrics.TryGetValue(key + "_avg_ms", out var avg) ? avg : null) + " / " +
                Number(metrics.TryGetValue(key + "_max_ms", out var max) ? max : null), MinWidth = 140 });
            _pvsRows.AddChild(row);
        }
        string Metric(string key, double factor = 1) => metrics.TryGetValue(key, out var value) ? Number(value / factor) : "N/A";
        _pvsRows.AddChild(Text($"Workers · суммарно за окно: build {Metric("pvs_build_worker_ms")} мс · write {Metric("pvs_write_worker_ms")} мс · send {Metric("pvs_send_worker_ms")} мс · replay {Metric("pvs_replay_worker_ms")} мс"));
        _pvsRows.AddChild(Text($"Измерений workers {Metric("pvs_worker_samples")} · отложено {Metric("pvs_deferred")} · максимум сущностей {Metric("pvs_entities_max")}"));
        _pvsRows.AddChild(Text($"Данные за окно: состояния {Metric("pvs_state_bytes", 1048576)} МиБ → сеть {Metric("pvs_wire_bytes", 1048576)} МиБ · максимум пакета {Metric("pvs_wire_bytes_max", 1024)} КиБ"));
        foreach (var counter in _memoryValues.Where(pair => pair.Key.StartsWith("pvs.", StringComparison.Ordinal)).OrderBy(pair => pair.Key).Take(24))
        {
            var row = Row(); row.AddChild(Value(counter.Key)); row.AddChild(new Label { Text = counter.Value.ToString("N0"), MinWidth = 110 });
            _pvsRows.AddChild(row);
        }
        _pvsRows.AddChild(Text("Обработчики компонентов PVS · выборки"));
        foreach (var component in data.PvsComponents.Take(32))
        {
            var body = Column(3);
            body.AddChild(Value(component.Name, Color.FromHex("#86DDD4")));
            body.AddChild(Text($"{component.Samples:N0} выборок · сумма {Number(component.TotalMs)} мс · среднее {Number(component.AverageMs)} мс · пик {Number(component.MaximumMs)} мс · worst entity {component.WorstEntity}"));
            _pvsRows.AddChild(Section("", body, "#1D2935"));
        }
        if (data.PvsComponents.Length == 0) _pvsRows.AddChild(Text("Нет выборок компонентов PVS."));
        foreach (var metric in data.ContextMetrics.OrderBy(row => row.Name).Take(64))
        {
            var row = Row(); row.AddChild(Value(metric.Name)); row.AddChild(new Label { Text = Number(metric.Value), MinWidth = 110 });
            _contextRows.AddChild(row);
        }
        if (data.ContextMetrics.Length == 0) _contextRows.AddChild(Text("Нет данных контекста и очередей."));
    }
}

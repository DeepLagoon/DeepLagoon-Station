using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Client.Eui;
using Content.Shared._DeepLagoon.Performance;
using Content.Shared.Eui;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Input;

namespace Content.Client._DeepLagoon.Diagnostics;

public sealed partial class PerformanceMonitorEui : BaseEui
{
    private readonly DefaultWindow _window;
    private readonly TabContainer _tabs;
    private readonly RichTextLabel _overview, _timing, _memory, _phaseText, _candidateTitle, _status;
    private readonly RichTextLabel _diagnosticStatus, _memoryStatus, _captureStatus, _systemStatus;
    private readonly BoxContainer _candidateRows, _systemRows, _memoryRows;
    private readonly List<PerformanceMonitorSample> _samples = new();
    private readonly HashSet<NetEntity> _selected = new();
    private readonly List<(CheckBox Check, NetEntity Entity)> _candidateChecks = new();
    private readonly Dictionary<string, double> _memoryValues = new();
    private readonly Dictionary<string, Label> _values = new();
    private readonly List<(Label Scale, SampleGraph Graph)> _graphs = new();
    private readonly List<(Button Button, PerformanceDiagnosticAction Action)> _actions = new();
    private readonly CheckBox _confirmPause;
    private readonly Button _delete;
    private PerformanceDiagnosticsState? _controls;
    private int _seconds = 120, _lastRound = -1;
    private OptionButton _rangeChoice = default!, _systemOrder = default!;
    private RichTextLabel _journalStatus = default!;
    private BoxContainer _pvsRows = default!, _contextRows = default!;
    private LineEdit _systemFilter = default!;
    private PerformanceMonitorState? _lastState;
    private static readonly int[] Ranges = [30,60,120,600,1800,3600,21600,86400,604800,0];

    public PerformanceMonitorEui()
    {
        _window = new DefaultWindow { Title = "Производительность раунда", MinWidth = 640, MinHeight = 520, SetSize = new Vector2(880, 740) };
        var root = Column(8);
        _window.Contents.AddChild(root);
        _overview = Text("Ожидание измерений…");
        root.AddChild(_overview);
        root.AddChild(HistoryToolbar());
        _tabs = new TabContainer { HorizontalExpand = true, VerticalExpand = true };
        root.AddChild(_tabs);
        _status = Text("");
        root.AddChild(_status);

        var overview = Tab("Обзор");
        var cards = new GridContainer { Columns = 3, HorizontalExpand = true };
        foreach (var (key, title) in new[] { ("tps", "TPS"), ("tick", "Тик · мс"), ("entities", "Сущности ECS"),
                     ("managed", "Управляемая · МиБ"), ("alloc", "Аллокации · МиБ/с"), ("gc", "Пауза GC · мс/окно") })
        {
            var card = Column(2);
            card.Margin = new Thickness(6);
            card.AddChild(Text(title));
            var value = new Label { Text = "—", FontColorOverride = Color.Cyan };
            value.AddStyleClass("LabelHeadingBigger");
            card.AddChild(value);
            _values[key] = value;
            cards.AddChild(new PanelContainer { Children = { card } });
        }
        overview.AddChild(cards);
        overview.AddChild(Chart("Частота симуляции", ["TPS"], [Color.Cyan], [sample => sample.Tps]));
        overview.AddChild(Chart("Время фаз · мс/тик", ["Tick", "Simulation", "Physics", "PVS"],
            [Color.White, Color.Cyan, Color.Orange, Color.LimeGreen],
            [sample => sample.TickMs, sample => sample.SimulationMs, sample => sample.PhysicsMs, sample => sample.GameStateMs]));
        _timing = Text("Нет измерений фаз.");
        overview.AddChild(_timing);

        var systems = Tab("Системы");
        _systemStatus = Text("Ожидание отчёта систем.");
        systems.AddChild(_systemStatus);
        var systemTools = Row();
        _systemFilter = new LineEdit { PlaceHolder = "Найти систему по имени", HorizontalExpand = true };
        _systemFilter.OnTextChanged += _ => RenderSystems();
        _systemOrder = new OptionButton { MinWidth = 160 };
        foreach (var text in new[] { "Среднее время", "Пик вызова", "Аллокации", "Ошибки" }) _systemOrder.AddItem(text);
        _systemOrder.OnItemSelected += e => { _systemOrder.SelectId(e.Id); RenderSystems(); };
        systemTools.AddChild(_systemFilter); systemTools.AddChild(_systemOrder);
        systems.AddChild(systemTools);
        _systemRows = Column(6);
        systems.AddChild(_systemRows);
        _phaseText = Text("");
        systems.AddChild(Section("Подробные фазы", _phaseText, "#1D2935"));
        _pvsRows = Column(5);
        systems.AddChild(Section("PVS · синхронизация мира", _pvsRows, "#172C35"));
        systems.AddChild(Chart("PVS · поток тика, мс/тик", ["Ожидание", "Сериализация", "Отправка"],
            [Color.Orange, Color.Cyan, Color.LimeGreen], [p => p.PvsWaitMs, p => p.PvsSerializeMs, p => p.PvsSendMs]));
        _contextRows = Column(4);
        systems.AddChild(Section("Контекст и очереди сервера", _contextRows, "#1D2935"));

        var memory = Tab("Память / GC");
        memory.AddChild(Chart("Память процесса · МиБ", ["Managed", "Working set"], [Color.Cyan, Color.Orange],
            [sample => sample.ManagedMiB, sample => sample.WorkingSetMiB]));
        memory.AddChild(Chart("Паузы GC · мс за измеренное окно", ["GC"], [Color.Orange], [sample => sample.GcPauseMs]));
        _memory = Text("");
        memory.AddChild(_memory);
        _memoryStatus = Text("Ожидание сводки памяти серверного движка.");
        memory.AddChild(_memoryStatus);
        _memoryRows = Column(3);
        memory.AddChild(_memoryRows);

        var diagnostics = Tab("Диагностика");
        _diagnosticStatus = Text("");
        diagnostics.AddChild(Section("Состояние записи", _diagnosticStatus, "#172C35"));
        var phaseSettings = Column(5);
        phaseSettings.AddChild(Actions(("0 · Выкл.", PerformanceDiagnosticAction.PhasesOff),
            ("1 · Сводки", PerformanceDiagnosticAction.PhasesSummary), ("2 · Подробно", PerformanceDiagnosticAction.PhasesDetailed)));
        phaseSettings.AddChild(Actions(("Отчёт: 10 с", PerformanceDiagnosticAction.PhaseInterval10),
            ("30 с", PerformanceDiagnosticAction.PhaseInterval30), ("60 с", PerformanceDiagnosticAction.PhaseInterval60)));
        diagnostics.AddChild(Section("CPU · фазы и системы", phaseSettings, "#1D2935"));
        var memorySettings = Column(5);
        memorySettings.AddChild(Actions(("0 · Выкл.", PerformanceDiagnosticAction.MemoryOff), ("1 · Сводки", PerformanceDiagnosticAction.MemorySummary),
            ("2 · Трассы", PerformanceDiagnosticAction.MemoryTraces), ("3 · Снимки", PerformanceDiagnosticAction.MemorySnapshots)));
        diagnostics.AddChild(Section("Память · GC и кеши", memorySettings, "#1D2935"));
        var traces = Column(5);
        traces.AddChild(Actions(("Авто-GC: выкл.", PerformanceDiagnosticAction.AutoTraceOff), ("Каждые 30 мин", PerformanceDiagnosticAction.AutoTraceOn)));
        traces.AddChild(Actions(("GC · 30 с", PerformanceDiagnosticAction.TraceGc), ("Аллокации · 30 с", PerformanceDiagnosticAction.TraceAllocations),
            ("Остановить запись", PerformanceDiagnosticAction.StopCapture)));
        _captureStatus = Text(""); traces.AddChild(_captureStatus);
        diagnostics.AddChild(Section("Трассировка", traces, "#172C35"));
        var snapshots = Column(5);
        _confirmPause = new CheckBox { Text = "Подтверждаю возможную паузу игры" };
        _confirmPause.OnToggled += _ => UpdateActions();
        snapshots.AddChild(_confirmPause);
        snapshots.AddChild(Actions(("Снимок heap", PerformanceDiagnosticAction.HeapSnapshot), ("Снимок gcdump", PerformanceDiagnosticAction.GcSnapshot)));
        diagnostics.AddChild(Section("Ручные снимки · уровень 3", snapshots, "#352A25"));

        var cleanup = Tab("Очистка");
        _candidateTitle = Text("Кандидаты на очистку");
        cleanup.AddChild(_candidateTitle);
        var selection = Row();
        var select = new Button { Text = "Выбрать просроченные", HorizontalExpand = true };
        select.OnPressed += _ =>
        {
            _selected.Clear();
            foreach (var row in _candidateChecks) row.Check.Pressed = false;
            foreach (var row in _candidateChecks.Where(row => !row.Check.Disabled).Take(32))
            {
                row.Check.Pressed = true;
                _selected.Add(row.Entity);
            }
            UpdateDelete();
        };
        var reset = new Button { Text = "Снять выбор", HorizontalExpand = true };
        reset.OnPressed += _ => { foreach (var row in _candidateChecks) row.Check.Pressed = false; _selected.Clear(); UpdateDelete(); };
        selection.AddChild(select); selection.AddChild(reset);
        cleanup.AddChild(selection);
        _delete = new Button { Text = "Удалить выбранные (0/32)", Disabled = true };
        _delete.OnPressed += _ => SendMessage(new PerformanceCleanupRequest(_selected.Take(32).ToArray()));
        cleanup.AddChild(_delete);
        _candidateRows = Column(3);
        cleanup.AddChild(_candidateRows);
        _window.OnClose += OnClose;
        UpdateActions();
    }

    private static BoxContainer Column(int separation = 8) => new()
        { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true, SeparationOverride = separation };
    private static BoxContainer Row() => new() { Orientation = BoxContainer.LayoutOrientation.Horizontal, HorizontalExpand = true, SeparationOverride = 6 };
    private static RichTextLabel Text(string text)
    {
        var label = new RichTextLabel { HorizontalExpand = true };
        label.SetMessage(text); // Plain text; names and diagnostic output are never interpreted as markup.
        return label;
    }
    private BoxContainer Tab(string title)
    {
        var body = Column(); body.Margin = new Thickness(10);
        var scroll = new ScrollContainer { HScrollEnabled = false, HorizontalExpand = true, VerticalExpand = true };
        scroll.AddChild(body);
        _tabs.AddChild(scroll);
        TabContainer.SetTabTitle(scroll, title);
        return body;
    }
    private BoxContainer Actions(params (string Text, PerformanceDiagnosticAction Action)[] actions)
    {
        var row = Row();
        foreach (var (text, action) in actions)
        {
            var button = new Button { Text = text, HorizontalExpand = true, ToggleMode = action is <= PerformanceDiagnosticAction.AutoTraceOn or >= PerformanceDiagnosticAction.PhaseInterval10 };
            button.OnPressed += _ =>
            {
                var confirmed = _confirmPause.Pressed;
                if (action is PerformanceDiagnosticAction.HeapSnapshot or PerformanceDiagnosticAction.GcSnapshot)
                { _confirmPause.Pressed = false; UpdateActions(); }
                SendMessage(new PerformanceDiagnosticRequest(action, confirmed));
            };
            _actions.Add((button, action));
            row.AddChild(button);
        }
        return row;
    }
    private BoxContainer Chart(string title, string[] names, Color[] colours, Func<PerformanceMonitorSample, double?>[] series)
    {
        var box = Column(3);
        box.AddChild(Text(title));
        var legend = Row();
        for (var i = 0; i < names.Length; i++) legend.AddChild(new Label { Text = "━ " + names[i], FontColorOverride = colours[i] });
        box.AddChild(legend);
        var graph = new SampleGraph(_samples, colours, series) { MinHeight = 138, HorizontalExpand = true, MouseFilter = Control.MouseFilterMode.Stop };
        graph.PointSelected += at => SendMessage(new PerformanceHistoryRequest(_seconds, at));
        box.AddChild(graph);
        var scale = new Label { Text = "Ожидание данных" };
        box.AddChild(scale);
        _graphs.Add((scale, graph));
        return box;
    }
    private void RefreshGraphs()
    {
        foreach (var (scale, graph) in _graphs)
        {
            graph.Seconds = _seconds;
            graph.RefreshScale();
            var span = _samples.Count > 1 ? (_seconds == 0 ? _samples[^1].Time - _samples[0].Time : Math.Min(_seconds, _samples[^1].Time - _samples[0].Time)) : 0;
            scale.Text = $"Y: 0–{Number(graph.Maximum)}  ·  X: {Duration(span)}" + (_samples.Count > 0 ? " · " + Date(_samples[^1].Time) : "");
        }
    }
    public override void Opened() => _window.OpenCentered();
    public override void Closed() { _window.OnClose -= OnClose; _window.Close(); }
    private void OnClose() => SendMessage(new CloseEuiMessage());
    private static string Number(double? value) => value is { } number && double.IsFinite(number)
        ? number.ToString("0.00", CultureInfo.InvariantCulture) : "N/A";
    private static string Level(int value, bool memory = false) => value switch
        { 0 => "выключено", 1 => "сводки", 2 => memory ? "трассы" : "подробно", 3 => "снимки разрешены", _ => "неизвестно" };

    public override void HandleState(EuiStateBase state)
    {
        if (state is not PerformanceMonitorState data) return;
        if (_lastRound != data.RoundId || data.ResetHistory) _samples.Clear();
        if (_lastRound != data.RoundId) { _memoryValues.Clear(); _selected.Clear(); }
        _lastRound = data.RoundId;
        _lastState = data;
        _seconds = data.HistorySeconds;
        var rangeIndex = Array.IndexOf(Ranges, _seconds);
        if (rangeIndex >= 0) _rangeChoice.SelectId(rangeIndex);
        _journalStatus.SetMessage((data.Historical ? "Срез " + Date(data.SelectedTime) : "В эфире") + " · " + data.JournalStatus);
        foreach (var sample in data.Samples)
            if (_samples.Count == 0 || sample.Time > _samples[^1].Time) _samples.Add(sample);
                if (_samples.Count > 2500) _samples.RemoveRange(0, _samples.Count - 2500);
        RefreshGraphs();
        _overview.SetMessage($"Раунд #{data.RoundId}  ·  ECS {data.Entities:N0}  ·  кандидаты {data.TrackedCandidates}");
        _values["entities"].Text = data.Entities >= 0 ? data.Entities.ToString("N0") : "N/A";
        if (_samples.LastOrDefault() is { } latest)
        {
            _values["tps"].Text = Number(latest.Tps);
            _values["tick"].Text = Number(latest.TickMs);
            _values["managed"].Text = Number(latest.ManagedMiB);
            _values["alloc"].Text = Number(latest.AllocatedMiBPerSecond);
            _values["gc"].Text = Number(latest.GcPauseMs);
            var age = latest.SummaryAgeSeconds < 0 ? "отчёт ещё не получен" : $"давность {latest.SummaryAgeSeconds:0} с";
            var freshness = $"Окно отчёта {latest.SummaryWindowSeconds:0} с · {age}.";
            _timing.SetMessage($"Tick {Number(latest.TickMs)} · simulation {Number(latest.SimulationMs)} · physics {Number(latest.PhysicsMs)} · PVS {Number(latest.GameStateMs)} мс/тик.\n{freshness}");
            _systemStatus.SetMessage(freshness);
            _memory.SetMessage($"Managed {Number(latest.ManagedMiB)} МиБ · working set {Number(latest.WorkingSetMiB)} МиБ\nАллокации {Number(latest.AllocatedMiBPerSecond)} МиБ/с · паузы GC {Number(latest.GcPauseMs)} мс за {latest.WindowSeconds:0.00} с.");
        }
        RenderSystems();
        _phaseText.SetMessage(data.Diagnostics && data.Phases.Length > 0
            ? "Подробные фазы · мс за выборку / вызовы / последний счётчик\n" + string.Join("\n", data.Phases.Take(32).Select(phase => $"{phase.Name}\n    {Number(phase.TotalMs)} мс / {phase.Calls} / {phase.Count}"))
            : "Подробные фазы: нет выборок.");
        if (data.MemoryAgeSeconds < 0) { _memoryValues.Clear(); _memoryRows.DisposeAllChildren(); }
        if (data.Memory.Length > 0)
        {
            _memoryValues.Clear();
            foreach (var metric in data.Memory) _memoryValues[metric.Name] = metric.Value;
            UpdateMemoryRows();
        }
        _memoryStatus.SetMessage(data.MemoryAgeSeconds < 0
            ? "Нет сводки памяти."
            : $"Расширенная сводка: давность {data.MemoryAgeSeconds:0} с.");
        RenderPvs(data);
        _controls = data.Controls;
        _diagnosticStatus.SetMessage($"Фазы: {Level(data.Controls.PhaseLevel)} · отчёт {data.Controls.PhaseInterval:0} с\nПамять: {(data.Controls.MemoryAvailable ? Level(data.Controls.MemoryLevel, true) : "недоступна в этой сборке")} · сводка {data.Controls.MemoryInterval} с · авто-GC трасса {(data.Controls.AutoTraceInterval > 0 ? $"раз в {data.Controls.AutoTraceInterval} с" : "выключена")}\n" +
            (data.Controls.CanConfigure ? "Управление: Debug + Host." : "Режим просмотра."));
        _captureStatus.SetMessage(data.Controls.CaptureStatus);
        UpdateActions();
        _status.SetMessage(data.Status);
        _candidateTitle.SetMessage(data.Historical ? "Исторический срез · очистка недоступна"
            : $"Кандидаты на очистку · показано {data.Candidates.Length} из {data.TrackedCandidates}");
        _selected.IntersectWith(data.Candidates.Where(candidate => !candidate.Paused && candidate.RemainingSeconds == 0).Select(candidate => candidate.Entity));
        _candidateRows.DisposeAllChildren();
        _candidateChecks.Clear();
        foreach (var candidate in data.Candidates)
        {
            var row = Column(1);
            var check = new CheckBox { Text = $"{candidate.Reason} · ×{candidate.Units} · {candidate.Entity}", Pressed = _selected.Contains(candidate.Entity),
                Disabled = candidate.Paused || candidate.RemainingSeconds > 0 };
            check.OnToggled += args =>
            {
                if (args.Pressed && _selected.Count < 32) _selected.Add(candidate.Entity);
                else { _selected.Remove(candidate.Entity); if (args.Pressed) check.Pressed = false; }
                UpdateDelete();
            };
            row.AddChild(check);
            _candidateChecks.Add((check, candidate.Entity));
            row.AddChild(Text(candidate.Name + (candidate.Paused ? " · пауза" : $" · возраст {candidate.AgeSeconds:0} с · до удаления {candidate.RemainingSeconds:0} с")));
            _candidateRows.AddChild(row);
        }
        UpdateDelete();
    }
    private void UpdateDelete() { _delete.Disabled = _selected.Count == 0; _delete.Text = $"Удалить выбранные ({_selected.Count}/32)"; }
    private void UpdateMemoryRows()
    {
        _memoryRows.DisposeAllChildren();
        string Metric(string name, bool bytes = false) => _memoryValues.TryGetValue(name, out var value) ? Number(bytes ? value / 1048576 : value) : "N/A";
        var collected = _memoryValues.GetValueOrDefault("last_gc_index") > 0;
        _memoryRows.AddChild(Text(collected ? $"GC #{Metric("last_gc_index")} · поколение {Metric("last_gc_generation")} · фрагментация {Metric("last_fragmented_bytes", true)} МиБ\nЗакреплённые объекты: {Metric("last_pinned")} · ожидающие финализации: {Metric("last_finalizers")}" : "GC: нет данных."));
        foreach (var generation in new[] { "gen0", "gen1", "gen2", "loh", "poh" })
            _memoryRows.AddChild(Text(collected ? $"{generation.ToUpperInvariant()} · после GC {Metric(generation + "_after_bytes", true)} МиБ · фрагменты {Metric(generation + "_fragmented_after_bytes", true)} МиБ" : generation.ToUpperInvariant() + " · N/A"));
        _memoryRows.AddChild(Text($"Стоимость сводки: поток тика {Metric("capture_main_ms")} мс · worker {Metric("previous_worker_ms")} мс · пропущено отчётов {Metric("reports_dropped")}"));
        _memoryRows.AddChild(Text("Счётчики владельцев"));
        foreach (var metric in _memoryValues.Where(pair => pair.Key.Contains('.')).OrderBy(pair => pair.Key).Take(128))
        {
            var row = Row(); row.AddChild(Text(metric.Key)); row.AddChild(new Label { Text = metric.Value.ToString("N0"), MinWidth = 96 });
            _memoryRows.AddChild(row);
        }
    }
    private void UpdateActions()
    {
        var state = _controls;
        foreach (var (button, action) in _actions)
        {
            var phase = action is <= PerformanceDiagnosticAction.PhasesDetailed or >= PerformanceDiagnosticAction.PhaseInterval10;
            button.Disabled = state == null || !state.CanConfigure || !phase && !state.MemoryAvailable;
            if (state == null) continue;
            var snapshot = action is PerformanceDiagnosticAction.HeapSnapshot or PerformanceDiagnosticAction.GcSnapshot;
            if (snapshot) button.Disabled |= state.MemoryLevel < 3 || !_confirmPause.Pressed;
            if (action is PerformanceDiagnosticAction.TraceGc or PerformanceDiagnosticAction.TraceAllocations) button.Disabled |= state.MemoryLevel < 2;
            button.Pressed = action switch
            {
                PerformanceDiagnosticAction.PhasesOff => state.PhaseLevel == 0,
                PerformanceDiagnosticAction.PhasesSummary => state.PhaseLevel == 1,
                PerformanceDiagnosticAction.PhasesDetailed => state.PhaseLevel >= 2,
                PerformanceDiagnosticAction.MemoryOff => state.MemoryLevel == 0,
                PerformanceDiagnosticAction.MemorySummary => state.MemoryLevel == 1,
                PerformanceDiagnosticAction.MemoryTraces => state.MemoryLevel == 2,
                PerformanceDiagnosticAction.MemorySnapshots => state.MemoryLevel >= 3,
                PerformanceDiagnosticAction.AutoTraceOff => state.AutoTraceInterval == 0,
                PerformanceDiagnosticAction.AutoTraceOn => state.AutoTraceInterval > 0,
                PerformanceDiagnosticAction.PhaseInterval10 => state.PhaseInterval == 10,
                PerformanceDiagnosticAction.PhaseInterval30 => state.PhaseInterval == 30,
                PerformanceDiagnosticAction.PhaseInterval60 => state.PhaseInterval == 60,
                _ => false,
            };
        }
    }
    private sealed class SampleGraph(List<PerformanceMonitorSample> samples, Color[] colours, Func<PerformanceMonitorSample, double?>[] series) : Control
    {
        public double Maximum { get; private set; } = 1;
        public int Seconds = 120;
        public event Action<double>? PointSelected;
        private double Start => samples.Count == 0 ? 0 : Seconds == 0 ? samples[0].Time : Math.Max(samples[0].Time, samples[^1].Time - Seconds);
        protected override void KeyBindDown(GUIBoundKeyEventArgs args)
        {
            base.KeyBindDown(args);
            if (args.Function != EngineKeyFunctions.UIClick || samples.Count == 0) return;
            var target = Start + Math.Clamp(args.RelativePosition.X / Math.Max(1, Width), 0, 1) * (samples[^1].Time - Start);
            var nearest = samples.Where(p => p.Time >= Start).MinBy(p => Math.Abs(p.Time - target));
            if (nearest != null) PointSelected?.Invoke(nearest.Time);
            args.Handle();
        }
        protected override void MouseMove(GUIMouseMoveEventArgs args)
        {
            base.MouseMove(args);
            if (samples.Count == 0) return;
            var target = Start + Math.Clamp(args.RelativePosition.X / Math.Max(1, Width), 0, 1) * (samples[^1].Time - Start);
            var nearest = samples.Where(p => p.Time >= Start).MinBy(p => Math.Abs(p.Time - target));
            if (nearest != null) ToolTip = Date(nearest.Time) + " · тик " + Number(nearest.TickMs) + " мс · GC " + Number(nearest.GcPauseMs) + " мс";
        }
        public void RefreshScale()
        {
            Maximum = 1;
            foreach (var selector in series)
                foreach (var sample in samples)
                    if (sample.Time >= Start && selector(sample) is { } value && double.IsFinite(value)) Maximum = Math.Max(Maximum, value);
        }
        protected override void Draw(DrawingHandleScreen handle)
        {
            base.Draw(handle);
            handle.DrawRect(UIBox2.FromDimensions(Vector2.Zero, new Vector2(PixelWidth, PixelHeight)), Color.FromHex("#151C25"));
            for (var line = 1; line < 4; line++) handle.DrawLine(new Vector2(0, PixelHeight * line / 4f), new Vector2(PixelWidth, PixelHeight * line / 4f), Color.FromHex("#2B3542"));
            if (samples.Count < 2) return;
            var start = Start;
            var duration = Math.Max(1, samples[^1].Time - start);
            for (var index = 0; index < series.Length; index++)
            {
                Vector2? previous = null;
                foreach (var sample in samples)
                {
                    if (sample.Time < start || series[index](sample) is not { } value || !double.IsFinite(value)) { previous = null; continue; }
                    var point = new Vector2((float)((sample.Time - start) / duration * PixelWidth), PixelHeight - (float)(Math.Clamp(value / Maximum, 0, 1) * (PixelHeight - 4)) - 2);
                    if (previous is { } from) handle.DrawLine(from, point, colours[index]);
                    else handle.DrawCircle(point, 1.5f, colours[index]);
                    previous = point;
                }
            }
        }
    }
}

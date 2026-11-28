using System.Collections.Generic;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Content.Shared._DeepLagoon.Performance;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using MonitorView = Content.Client._DeepLagoon.Diagnostics.PerformanceMonitorEui;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class PerformanceMonitorWindowTests
{
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (var child in root.Children) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }

    [Test]
    public async Task TabsWrapAtSmallSizeAndCleanupSelectionIsBounded()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        MonitorView view = null!;
        DefaultWindow window = null!;
        Button select = null!, reset = null!;
        await pair.Client.WaitAssertion(() =>
        {
            view = new MonitorView();
            window = Field<DefaultWindow>(view, "_window");
            var tabs = Field<TabContainer>(view, "_tabs");
            view.Opened();
            var state = new PerformanceMonitorState(
                [new(1, 1, 30, 950, 1200, 4, 70, 15, 10, 3, 4, 60, 16), new(2, 1, 30, 960, 1201, 5, 0, 15, 10, 3, 4, 60, 17)], [],
                Enumerable.Range(1, 40).Select(index => new PerformanceCleanupCandidate(new NetEntity(index), new string('x', 160), "Предмет в космосе", 901, 0, 1, false)).ToArray(),
                4080, 40, true, "Длинный статус с результатом действия администратора.")
            {
                RoundId = 214,
                Controls = new(2, 60, 3, 60, 1800, true, true, "status=idle directory=/a/very/long/path/" + new string('x', 160)),
                Systems = [new(new string('s', 150), 7, 50)],
                Memory = [new("last_gc_index", 4), new("loh_after_bytes", 16777216), new("ecs.entities", 4080)],
                MemoryAgeSeconds = 2,
            };
            view.HandleState(state);
            var memoryRows = Field<BoxContainer>(view, "_memoryRows");
            var count = memoryRows.ChildCount;
            state.Memory = []; // Delta updates must preserve the last actual memory report.
            view.HandleState(state);
            Assert.That(memoryRows.ChildCount, Is.EqualTo(count));
            Assert.That(tabs.ChildCount, Is.EqualTo(5));
            foreach (var width in new[] { 980, 640 })
                for (var tab = 0; tab < tabs.ChildCount; tab++)
                {
                    tabs.CurrentTab = tab;
                    var size = new Vector2(width, 600);
                    window.SetSize = size;
                    window.Measure(size);
                    window.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
                    var scroll = (ScrollContainer)tabs.GetChild(tab);
                    Assert.That(scroll.HScrollEnabled, Is.False);
                    Assert.That(scroll.Width, Is.LessThanOrEqualTo(width));
                    foreach (var control in Descendants(scroll).Where(control => control.Size.X > 0))
                        Assert.That(control.GlobalPosition.X + control.Width, Is.LessThanOrEqualTo(scroll.GlobalPosition.X + scroll.Width + 1),
                            $"{width}px tab{tab}: {control.GetType().Name} exceeds the visible width.");
                }
            var buttons = Descendants(window).OfType<Button>().ToArray();
            var heap = buttons.Single(button => button.Text == "Снимок heap");
            Assert.That(heap.Disabled, Is.True, "Level 3 alone must not permit a snapshot without explicit confirmation.");
            var confirm = Field<CheckBox>(view, "_confirmPause");
            confirm.Pressed = true;
            view.HandleState(state);
            Assert.That(heap.Disabled, Is.False);
            confirm.Pressed = false;
            view.HandleState(state);
            Assert.That(heap.Disabled, Is.True);
            select = buttons.Single(button => button.Text == "Выбрать просроченные");
            reset = buttons.Single(button => button.Text == "Снять выбор");
        });
        async Task Click(Button button)
        {
            var position = new ScreenCoordinates(button.GlobalPixelPosition + button.PixelSize / 2, button.Window!.Id);
            foreach (var state in new[] { BoundKeyState.Down, BoundKeyState.Up })
                await pair.Client.DoGuiEvent(button, new GUIBoundKeyEventArgs(EngineKeyFunctions.UIClick, state, position, default, Vector2.Zero, Vector2.Zero));
        }
        await Click(select);
        await pair.Client.WaitAssertion(() => Assert.That(Field<HashSet<NetEntity>>(view, "_selected"), Has.Count.EqualTo(32)));
        await Click(reset);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(Field<HashSet<NetEntity>>(view, "_selected"), Is.Empty);
            view.Closed(); window.Dispose();
        });
        await pair.CleanReturnAsync();
    }
}

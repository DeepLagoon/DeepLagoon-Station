using Content.Client.UserInterface.Systems.Chat.Controls;
using Content.Shared._DeepLagoon.LustWish;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;

namespace Content.Client._DeepLagoon.LustWish;

public sealed partial class LustWishColorSystem : EntitySystem
{
    private DefaultWindow? _window;
    public override void Initialize() => SubscribeNetworkEvent<LustWishColorPickerEvent>(OnPicker);

    private void OnPicker(LustWishColorPickerEvent ev)
    {
        _window?.Close();
        var window = _window = new DefaultWindow { Title = ev.Name, MinWidth = 260 };
        var box = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 6 };
        var wheel = new ChatColorWheel { SelectedColor = ev.Color.WithAlpha(1f), HorizontalExpand = true, MinHeight = 220 };
        var selected = ev.Color.WithAlpha(1f);
        var brightness = new Slider { MinValue = 0, MaxValue = 1, Value = Color.ToHsv(selected).Z, HorizontalExpand = true };
        wheel.OnColorChanged += c => selected = c.WithAlpha(1f);
        brightness.OnValueChanged += _ => wheel.SetBrightness(brightness.Value);
        var apply = new Button { Text = Loc.GetString("dl-lustwish-color-apply") };
        apply.OnPressed += _ => { RaiseNetworkEvent(new LustWishSetColorEvent(ev.Item, selected.ToHexNoAlpha())); window.Close(); };
        box.AddChild(wheel); box.AddChild(new Label { Text = Loc.GetString("chat-panel-brightness") });
        box.AddChild(brightness); box.AddChild(apply); window.Contents.AddChild(box);
        window.OnClose += () => { if (_window == window) _window = null; };
        window.OpenCentered();
    }

    public override void Shutdown() { _window?.Close(); _window = null; base.Shutdown(); }
}

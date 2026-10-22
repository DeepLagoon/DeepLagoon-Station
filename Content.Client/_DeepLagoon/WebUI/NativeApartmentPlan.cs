using System.Linq;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;

namespace Content.Client._DeepLagoon.WebUI;

internal sealed class NativeApartmentPlan : Control
{
    private NativeUiData _state = new();
    private NativeUiData[] _draft = [];

    public NativeApartmentPlan()
    {
        MinSize = new Vector2(300, 250);
        HorizontalExpand = true;
    }

    public void Update(NativeUiData state, IEnumerable<TguiData> draft)
    {
        _state = state;
        _draft = draft.Select(item => NativeUiData.Parse(item.ToString())).ToArray();
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        handle.DrawRect(PixelSizeBox, Color.FromHex("#101922"));
        var width = _state.Int("width");
        var height = _state.Int("height");
        if (width <= 0 || height <= 0) return;
        var scene = _state.Object("scene");
        var scale = Math.Min(PixelSize.X / (float) width, PixelSize.Y / (float) height);
        var origin = new Vector2((PixelSize.X - width * scale) / 2, (PixelSize.Y - height * scale) / 2);
        UIBox2 Cell(int x, int y, int w = 1, int h = 1)
        {
            var low = origin + new Vector2(x - scene.Int("x"), scene.Int("y") + height - y - h) * scale;
            return UIBox2.FromDimensions(low, new Vector2(w, h) * scale);
        }
        foreach (var tile in scene.Array("tiles")) handle.DrawRect(Cell(tile.Int("x"), tile.Int("y")), Color.FromHex("#766349"));
        foreach (var item in scene.Array("occupied")) handle.DrawRect(Cell(item.Int("x"), item.Int("y")), Color.FromHex("#557384"));
        foreach (var placement in _draft)
        {
            var definition = _state.Array("catalog").FirstOrDefault(item => item.String("id") == placement.String("furniture"));
            if (definition == null) continue;
            var w = definition.Int("width");
            var h = definition.Int("height");
            if (placement.Int("rotation") % 2 != 0) (w, h) = (h, w);
            handle.DrawRect(Cell(placement.Int("x"), placement.Int("y"), w, h), Color.FromHex("#59BF93"));
        }
        handle.DrawRect(Cell(_state.Int("arrivalX"), _state.Int("arrivalY")), Color.FromHex("#70CACC"), filled: false);
    }
}

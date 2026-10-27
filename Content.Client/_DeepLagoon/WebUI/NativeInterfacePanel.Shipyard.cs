using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Maths;

namespace Content.Client._DeepLagoon.WebUI;

public sealed partial class NativeInterfacePanel
{
    private void Shipyard()
    {
        Text(_body, _data.String("name"));
        Text(_body, _data.String("description"));
        if (_data.String("error") is { Length: > 0 } error) { Text(_body, error); return; }
        _body.AddChild(new NativeShipyardPlan(_data));
        Text(_body, "Серый — пол, светлый — стены, синий — двери, жёлтый — оборудование.");
    }
}

internal sealed class NativeShipyardPlan : Control
{
    private readonly NativeUiData _data;

    public NativeShipyardPlan(NativeUiData data)
    {
        _data = data;
        MinSize = new Vector2(240, 360);
        HorizontalExpand = true;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        var width = Math.Max(1, _data.Float("width"));
        var height = Math.Max(1, _data.Float("height"));
        var scale = Math.Min(Math.Max(1, PixelSize.X - 16) / width, Math.Max(1, PixelSize.Y - 16) / height);
        var origin = (new Vector2(PixelSize.X, PixelSize.Y) - new Vector2(width, height) * scale) / 2;
        var x = _data.Float("x");
        var y = _data.Float("y");
        void Cell(NativeUiData cell, Color color, float inset = 0)
        {
            var position = origin + new Vector2(cell.Float("x") - x, y + height - 1 - cell.Float("y")) * scale;
            handle.DrawRect(UIBox2.FromDimensions(position + new Vector2(scale * inset), new Vector2(scale * (1 - inset * 2))), color);
        }
        foreach (var tile in _data.Array("tiles")) Cell(tile, Color.FromHex("#596575"));
        foreach (var structure in _data.Array("structures")) Cell(structure, Color.FromHex(structure.Bool("door") ? "#568FD0" : "#C6D0DB"));
        foreach (var item in _data.Array("items"))
            if (item.String("kind") == "entity") Cell(item, Color.FromHex("#F0C875"), 0.25f);
    }
}

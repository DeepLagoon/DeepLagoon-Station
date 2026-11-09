using System.Linq;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Input;

namespace Content.Client._DeepLagoon.WebUI;

internal sealed class NativeParkingDockMap : Control
{
    private readonly Vector2[][] _station;
    private readonly Vector2[][] _ship;
    private readonly Port[] _ports;
    private readonly Vector2 _worldCenter;
    private readonly Vector2 _worldSize;
    private readonly View _view;
    private (Vector2 Start, Vector2 Last, bool Moved)? _drag;

    private const float DragThreshold = 5f;
    private static readonly Vector2 FlipY = new(1, -1);

    public sealed class View
    {
        public float Zoom = 1f;
        public Vector2 CenterOffset;
    }

    private sealed record Port(string Id, Vector2 Position, Color Color, Vector2? ShipOrigin, Angle ShipAngle);

    public string Selected = "";
    public event Action<string>? OnSelect;

    public NativeParkingDockMap(NativeUiData data, View view)
    {
        _view = view;
        _worldSize = new Vector2(Math.Max(1, data.Float("width")), Math.Max(1, data.Float("height")));
        _worldCenter = Point(data) + _worldSize / 2;
        _station = ReadPolygons(data, "station");
        _ship = ReadPolygons(data, "ship");
        _ports = data.Array("ports").Select(port => new Port(port.String("id"), Point(port), port.String("status") switch
        {
            "fits" => Color.FromHex("#69DDB5"),
            "occupied" => Color.FromHex("#E9B85F"),
            _ => Color.FromHex("#FF626E"),
        }, port.Bool("hasPose") ? new Vector2(port.Float("shipX"), port.Float("shipY")) : null,
            new Angle(port.Float("shipAngle")))).ToArray();
        MinHeight = 300;
        HorizontalExpand = true;
        RectClipContent = true;
        MouseFilter = MouseFilterMode.Stop;
        DefaultCursorShape = CursorShape.Move;
    }

    private static Vector2 Point(NativeUiData data) => new(data.Float("x"), data.Float("y"));

    private static Vector2[][] ReadPolygons(NativeUiData data, string field) =>
        data.Array(field).Select(polygon => polygon.Array("points").Select(Point).ToArray()).ToArray();

    private float FitScale => Math.Min(PixelSize.X / _worldSize.X, PixelSize.Y / _worldSize.Y);
    private Vector2 ScreenCenter => (Vector2) PixelSize / 2;

    private Matrix3x2 MapToScreen =>
        Matrix3x2.CreateTranslation(-(_worldCenter + _view.CenterOffset))
        * Matrix3x2.CreateScale(FitScale * _view.Zoom * FlipY)
        * Matrix3x2.CreateTranslation(ScreenCenter);

    public void ResetView()
    {
        _view.Zoom = 1f;
        _view.CenterOffset = Vector2.Zero;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        handle.DrawRect(PixelSizeBox, Color.FromHex("#101922"));
        var toScreen = MapToScreen;
        var previous = handle.GetTransform();
        handle.SetTransform(toScreen * previous);
        foreach (var polygon in _station)
            handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, polygon, Color.FromHex("#516B7C"));

        var selected = _ports.FirstOrDefault(port => port.Id == Selected);
        if (selected?.ShipOrigin is { } origin)
        {
            handle.SetTransform(Matrix3x2.CreateRotation((float) selected.ShipAngle.Theta)
                * Matrix3x2.CreateTranslation(origin) * toScreen * previous);
            foreach (var polygon in _ship)
            {
                handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, polygon, selected.Color.WithAlpha(0.55f));
                handle.DrawPrimitives(DrawPrimitiveTopology.LineLoop, polygon, selected.Color);
            }
        }

        handle.SetTransform(previous);
        foreach (var port in _ports)
        {
            var position = Vector2.Transform(port.Position, toScreen);
            handle.DrawCircle(position, 7 * UIScale, port == selected ? Color.White : Color.FromHex("#101922"));
            handle.DrawCircle(position, 5 * UIScale, port.Color);
        }
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);
        if (args.Function != EngineKeyFunctions.UIClick)
            return;

        _drag = (args.RelativePixelPosition, args.RelativePixelPosition, false);
        args.Handle();
    }

    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);
        if (_drag is not { } drag || !drag.Moved && !IsDrag(drag.Start, args.RelativePixelPosition))
            return;

        var scale = FitScale * _view.Zoom;
        if (scale <= 0)
            return;

        _view.CenterOffset -= (args.RelativePixelPosition - drag.Last) * FlipY / scale;
        _drag = (drag.Start, args.RelativePixelPosition, true);
        args.Handle();
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        base.KeyBindUp(args);
        if (args.Function != EngineKeyFunctions.UIClick || _drag is not { } drag)
            return;

        _drag = null;
        args.Handle();
        if (!drag.Moved && !IsDrag(drag.Start, args.RelativePixelPosition) && HasPoint(args.RelativePosition))
            SelectPort(args.RelativePixelPosition);
    }

    protected override void ControlFocusExited()
    {
        base.ControlFocusExited();
        _drag = null;
    }

    protected override void MouseWheel(GUIMouseWheelEventArgs args)
    {
        base.MouseWheel(args);
        if (args.Delta.Y == 0)
            return;

        args.Handle();
        var fit = FitScale;
        if (_drag != null || fit <= 0)
            return;

        var zoom = Math.Clamp(_view.Zoom * MathF.Pow(1.2f, args.Delta.Y), 1f, 16f);
        var shift = 1f / _view.Zoom - 1f / zoom;
        _view.CenterOffset += (args.RelativePixelPosition - ScreenCenter) * FlipY * shift / fit;
        _view.Zoom = zoom;
    }

    private bool IsDrag(Vector2 start, Vector2 cursor) =>
        Vector2.DistanceSquared(start, cursor) >= DragThreshold * DragThreshold * UIScale * UIScale;

    private void SelectPort(Vector2 cursor)
    {
        if (OnSelect == null)
            return;

        var toScreen = MapToScreen;
        var bestDistance = 100 * UIScale * UIScale;
        Port? closest = null;
        foreach (var port in _ports)
        {
            var distance = Vector2.DistanceSquared(cursor, Vector2.Transform(port.Position, toScreen));
            if (distance >= bestDistance)
                continue;
            closest = port;
            bestDistance = distance;
        }

        if (closest == null)
            return;
        OnSelect(closest.Id);
    }
}

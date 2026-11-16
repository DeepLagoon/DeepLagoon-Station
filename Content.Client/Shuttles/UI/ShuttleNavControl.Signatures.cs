using System.Numerics;
using Content.Shared.Shuttles.Components;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Collections;
using Robust.Shared.Input;

namespace Content.Client.Shuttles.UI;

public partial class ShuttleNavControl
{
    private const float SignatureNameScale = 0.9f;
    private const float SignatureDetailScale = 0.7f;
    private static readonly Color SignatureButtonColor = Color.FromHex("#12252f");
    private static readonly Color SignatureBorderColor = Color.FromHex("#679aa9");

    private readonly List<RadarSignature> _signatures = new();
    private readonly List<RadarSignature> _crowdedSignatures = new();
    private readonly List<PlacedSignature> _placedSignatures = new();
    private readonly List<UIBox2> _occupiedLabelBounds = new();
    private readonly List<string> _offscreenSignatures = new();
    private readonly List<bool> _assignedSignatures = new();
    private readonly List<int> _signatureOrder = new();
    private readonly List<int> _signatureGroupEnds = new();
    private readonly List<SignatureButton> _signatureButtons = new();
    private readonly List<string> _popupNames = new();
    private EntityUid? _pressedSignature;
    private EntityUid _openSignature;
    private Popup? _signaturePopup;
    private Label? _signaturePopupLabel;

    internal readonly record struct RadarSignature(EntityUid Entity, Vector2 Point, string Name, string Main,
        string? Affiliation, string? Coordinates, string? Track, Color Color, UIBox2 Bounds, Vector2 NameSize);

    private readonly record struct PlacedSignature(RadarSignature Signature, UIBox2 Bounds, bool NameOnly);
    private readonly record struct SignatureButton(EntityUid Entity, UIBox2 Bounds, int Start, int Count);

    private float? SignatureViewportRadius => CircularRadar ? ScaledMinimapRadius : null;

    internal static bool SignatureInView(Vector2 point, Vector2 size, bool circular, float radius)
    {
        return circular
            ? Vector2.DistanceSquared(point, size / 2) <= radius * radius
            : UIBox2.FromDimensions(Vector2.Zero, size).Contains(point);
    }

    private void CollectSignature(DrawingHandleScreen handle, EntityUid uid, Vector2 point, Vector2 worldPosition,
        float distance, string label, Color color, bool hidden, IFFComponent? iff, bool beacon)
    {
        var newline = label.IndexOf('\n');
        var name = newline < 0 ? label : label[..newline];
        if (!SignatureInView(point, PixelSize, CircularRadar, ScaledMinimapRadius))
        {
            _offscreenSignatures.Add(name);
            return;
        }

        var displayedDistance = distance < 50f ? $"{distance:0.0}" : distance < 1000 ? $"{distance:0}" : $"{distance / 1000:0.0}k";
        var distanceLabel = Loc.GetString("shuttle-console-iff-label", ("name", newline < 0 ? name : label[(newline + 1)..]),
            ("distance", displayedDistance));
        var main = newline < 0 ? distanceLabel : name;
        var affiliation = ShowIFFDetailed
            ? hidden ? Loc.GetString("shuttle-console-company-unknown") : newline < 0 ? null : distanceLabel
            : null;
        var coordinates = ShowIFFDetailed ? $"({worldPosition.X:0.0}, {worldPosition.Y:0.0})" : null;
        var track = ShowIFFDetailed && !beacon && iff != null ? Loc.GetString("shuttle-console-track-label") + iff.Address : null;

        var dimensions = handle.GetDimensions(Font, main, SignatureNameScale * UIScale);
        var size = dimensions;
        AddDetailSize(affiliation);
        AddDetailSize(coordinates);
        AddDetailSize(track);

        var markerPadding = RadarBlipSize * 0.7f * UIScale;
        var right = point + new Vector2(markerPadding, -dimensions.Y / 2);
        var left = right - new Vector2(size.X + 2 * markerPadding, 0);
        var labelBounds = PlaceSignature(right, size);
        var leftBounds = PlaceSignature(left, size);
        if (Vector2.DistanceSquared(leftBounds.TopLeft, left) < Vector2.DistanceSquared(labelBounds.TopLeft, right))
            labelBounds = leftBounds;

        var nameSize = name == main ? dimensions : handle.GetDimensions(Font, name, SignatureNameScale * UIScale);
        _signatures.Add(new RadarSignature(uid, point, name, main, affiliation, coordinates, track, color, labelBounds, nameSize));

        void AddDetailSize(string? detail)
        {
            if (detail == null)
                return;

            var detailSize = handle.GetDimensions(Font, detail, SignatureDetailScale * UIScale);
            size.X = MathF.Max(size.X, detailSize.X);
            size.Y += detailSize.Y;
        }
    }

    private UIBox2 PlaceSignature(Vector2 position, Vector2 size)
    {
        position = Vector2.Clamp(position, Vector2.Zero, Vector2.Max(Vector2.Zero, (Vector2) PixelSize - size));
        var bounds = UIBox2.FromDimensions(position, size);
        if (!CircularRadar)
            return bounds;

        var center = (Vector2) PixelSize / 2;
        var offset = bounds.Center - center;
        var halfSize = size / 2;
        var farCorner = Vector2.Abs(offset) + halfSize;
        var radius = ScaledMinimapRadius;
        if (farCorner.LengthSquared() <= radius * radius)
            return bounds;

        // Find the furthest position in this direction at which the whole label fits through the glass.
        var distance = offset.Length();
        if (distance == 0)
            return bounds;

        var direction = offset / distance;
        var projection = Vector2.Dot(Vector2.Abs(direction), halfSize);
        var maxDistance = MathF.Sqrt(MathF.Max(0, projection * projection + radius * radius - halfSize.LengthSquared())) - projection;
        return UIBox2.FromDimensions(center + direction * MathF.Max(0, maxDistance) - halfSize, size);
    }

    internal static bool TryFindLabelBounds(Vector2 point, Vector2 size, float lineHeight, float markerRadius,
        Vector2 viewport, float? radius, float scale, List<UIBox2> occupied, out UIBox2 bounds)
    {
        var maxPosition = Vector2.Max(Vector2.Zero, viewport - size);
        var center = viewport / 2;
        var halfSize = size / 2;
        var labelMargin = new Vector2(2 * scale);

        // Try each side of the marker before allowing a longer connector.
        for (var ring = 0; ring < 2; ring++)
        {
            var padding = markerRadius + (3 + ring * 14) * scale;
            for (var side = 0; side < 8; side++)
            {
                var offset = side switch
                {
                    0 => new Vector2(padding, -lineHeight / 2),
                    1 => new Vector2(-padding - size.X, -lineHeight / 2),
                    2 => new Vector2(-halfSize.X, -padding - size.Y),
                    3 => new Vector2(-halfSize.X, padding),
                    4 => new Vector2(padding, -padding - size.Y),
                    5 => new Vector2(-padding - size.X, -padding - size.Y),
                    6 => new Vector2(padding, padding),
                    _ => new Vector2(-padding - size.X, padding),
                };

                var position = Vector2.Clamp(point + offset, Vector2.Zero, maxPosition);
                var candidate = UIBox2.FromDimensions(position, size);
                if (candidate.Right > viewport.X || candidate.Bottom > viewport.Y)
                    continue;

                var farCorner = Vector2.Abs(candidate.Center - center) + halfSize;
                if (radius is { } limit && farCorner.LengthSquared() > limit * limit)
                    continue;

                var withMargin = new UIBox2(candidate.TopLeft - labelMargin, candidate.BottomRight + labelMargin);
                if (LabelBoundsOccupied(withMargin, occupied))
                    continue;

                bounds = candidate;
                return true;
            }
        }

        bounds = default;
        return false;
    }

    private static bool LabelBoundsOccupied(UIBox2 bounds, List<UIBox2> occupied)
    {
        foreach (var other in occupied)
        {
            if (bounds.Intersects(other))
                return true;
        }

        return false;
    }

    // The signatures are sorted top to bottom. Walk the connected components so that A overlapping B
    // and B overlapping C is a single group even when A and C do not touch.
    internal static void GroupOverlappingSignatures(List<RadarSignature> signatures, List<bool> assigned, List<int> order, List<int> ends)
    {
        signatures.Sort(static (a, b) =>
        {
            var vertical = a.Bounds.Top.CompareTo(b.Bounds.Top);
            if (vertical != 0)
                return vertical;

            var horizontal = a.Bounds.Left.CompareTo(b.Bounds.Left);
            return horizontal != 0 ? horizontal : a.Entity.CompareTo(b.Entity);
        });
        order.Clear();
        ends.Clear();
        assigned.Clear();
        for (var i = 0; i < signatures.Count; i++)
            assigned.Add(false);

        for (var i = 0; i < signatures.Count; i++)
        {
            if (assigned[i])
                continue;

            assigned[i] = true;
            var start = order.Count;
            order.Add(i);
            for (var cursor = start; cursor < order.Count; cursor++)
            {
                var bounds = signatures[order[cursor]].Bounds;
                for (var candidate = i + 1; candidate < signatures.Count && signatures[candidate].Bounds.Top <= bounds.Bottom; candidate++)
                {
                    if (assigned[candidate] || !bounds.Intersects(signatures[candidate].Bounds))
                        continue;

                    assigned[candidate] = true;
                    order.Add(candidate);
                }
            }
            ends.Add(order.Count);
        }
    }

    private void DrawSignatures(DrawingHandleScreen handle)
    {
        _crowdedSignatures.Clear();
        _placedSignatures.Clear();

        var markerScale = RadarBlipSize * UIScale;
        var markerRadius = markerScale * (2f / 3f);
        foreach (var signature in _signatures)
        {
            var point = signature.Point;
            _occupiedLabelBounds.Add(new UIBox2(
                point + new Vector2(-0.5f, -1f / 3f) * markerScale,
                point + new Vector2(0.5f, 2f / 3f) * markerScale));
        }

        if (_offscreenSignatures.Count > 0)
            DrawSignatureButton(handle, EntityUid.Invalid, new Vector2(PixelSize.X / 2f, PixelSize.Y), 0, _offscreenSignatures.Count);

        // Keep the placement order stable as markers move with the map.
        _signatures.Sort(static (a, b) => a.Entity.CompareTo(b.Entity));
        foreach (var signature in _signatures)
        {
            if (!PlaceLabel(signature, nameOnly: false))
                _crowdedSignatures.Add(signature);
        }

        // Use a shorter label before falling back to the popup, once all full labels have been tried.
        for (var i = 0; i < _crowdedSignatures.Count; i++)
        {
            var signature = _crowdedSignatures[i];
            if (signature.NameSize == signature.Bounds.Size || !PlaceLabel(signature, nameOnly: true))
                continue;

            _crowdedSignatures.RemoveAt(i);
            i--;
        }

        foreach (var placed in _placedSignatures)
            DrawLabelConnector(handle, placed.Signature.Point, placed.Bounds, placed.Signature.Color);

        DrawSignatureMarkers(handle);
        foreach (var placed in _placedSignatures)
            DrawSignatureDetails(handle, placed);

        GroupOverlappingSignatures(_crowdedSignatures, _assignedSignatures, _signatureOrder, _signatureGroupEnds);
        var start = 0;
        foreach (var end in _signatureGroupEnds)
        {
            var first = _crowdedSignatures[_signatureOrder[start]];
            var entity = first.Entity;
            for (var i = start + 1; i < end; i++)
            {
                var candidate = _crowdedSignatures[_signatureOrder[i]].Entity;
                if (candidate.Id < entity.Id)
                    entity = candidate;
            }

            DrawSignatureButton(handle, entity, first.Bounds.TopLeft, start, end - start, first.Point, markerRadius);
            start = end;
        }

        bool PlaceLabel(RadarSignature signature, bool nameOnly)
        {
            var size = nameOnly ? signature.NameSize : signature.Bounds.Size;
            if (!TryFindLabelBounds(signature.Point, size, signature.NameSize.Y, markerRadius, PixelSize,
                    SignatureViewportRadius, UIScale, _occupiedLabelBounds, out var bounds))
                return false;

            _placedSignatures.Add(new PlacedSignature(signature, bounds, nameOnly));
            _occupiedLabelBounds.Add(bounds);
            return true;
        }
    }

    private static void DrawLabelConnector(DrawingHandleScreen handle, Vector2 point, UIBox2 bounds, Color color)
    {
        var end = Vector2.Clamp(point, bounds.TopLeft, bounds.BottomRight);
        handle.DrawLine(point, end, color.WithAlpha(0.75f));
    }

    private void DrawSignatureMarkers(DrawingHandleScreen handle)
    {
        if (_signatures.Count == 0)
            return;

        var vertices = new Dictionary<Color, ValueList<Vector2>>();
        var scale = RadarBlipSize * UIScale;
        foreach (var signature in _signatures)
        {
            vertices.TryGetValue(signature.Color, out var batch);
            batch.Add(signature.Point + new Vector2(-0.5f, -1f / 3f) * scale);
            batch.Add(signature.Point + new Vector2(0.5f, -1f / 3f) * scale);
            batch.Add(signature.Point + new Vector2(0, 2f / 3f) * scale);
            vertices[signature.Color] = batch;
        }

        foreach (var (color, batch) in vertices)
            DrawPrimitiveBatches(handle, DrawPrimitiveTopology.TriangleList, batch.Span, color);
    }

    private void DrawSignatureDetails(DrawingHandleScreen handle, PlacedSignature placed)
    {
        var signature = placed.Signature;
        var position = placed.Bounds.TopLeft;
        var title = placed.NameOnly ? signature.Name : signature.Main;
        handle.DrawString(Font, position, title, SignatureNameScale * UIScale, signature.Color);
        if (placed.NameOnly)
            return;

        position.Y += Font.GetLineHeight(SignatureNameScale * UIScale);
        DrawDetail(signature.Affiliation);
        DrawDetail(signature.Coordinates);
        DrawDetail(signature.Track);

        void DrawDetail(string? detail)
        {
            if (detail == null)
                return;

            handle.DrawString(Font, position, detail, SignatureDetailScale * UIScale, signature.Color);
            position.Y += Font.GetLineHeight(SignatureDetailScale * UIScale);
        }
    }

    private void DrawSignatureButton(DrawingHandleScreen handle, EntityUid entity, Vector2 position, int start, int count,
        Vector2? marker = null, float markerRadius = 0)
    {
        var arrow = _signaturePopup != null && _openSignature == entity ? " ▴" : " ▾";
        var text = Loc.GetString("shuttle-radar-signatures", ("count", count)) + arrow;
        var scale = SignatureNameScale * UIScale;
        var padding = new Vector2(6, 3) * UIScale;
        var size = handle.GetDimensions(Font, text, scale) + 2 * padding;
        if (entity == EntityUid.Invalid)
            position -= new Vector2(size.X / 2, size.Y + 8 * UIScale);

        if (marker is not { } point || !TryFindLabelBounds(point, size, size.Y, markerRadius, PixelSize,
                SignatureViewportRadius, UIScale, _occupiedLabelBounds, out var bounds))
            bounds = PlaceSignature(position, size);

        _occupiedLabelBounds.Add(bounds);
        var pointer = (UserInterfaceManager.MousePositionScaled.Position - GlobalPosition) * UIScale;
        var hovered = bounds.Contains(pointer);
        handle.DrawRect(bounds, hovered ? SignatureBorderColor : SignatureButtonColor);
        handle.DrawRect(bounds, SignatureBorderColor, filled: false);
        handle.DrawString(Font, bounds.TopLeft + padding, text, scale, Color.White);
        _signatureButtons.Add(new SignatureButton(entity, bounds, start, count));
    }

    protected bool IsSignatureGroupAt(Vector2 position) => GetSignatureButton(position).HasValue;

    private bool TryPressSignatureGroup(GUIBoundKeyEventArgs args)
    {
        if (args.Function != EngineKeyFunctions.UIClick)
            return false;

        _pressedSignature = GetSignatureButton(args.RelativePosition)?.Entity;
        if (_pressedSignature == null)
            return false;

        _isMouseDown = false;
        args.Handle();
        return true;
    }

    private bool TryReleaseSignatureGroup(GUIBoundKeyEventArgs args)
    {
        if (args.Function != EngineKeyFunctions.UIClick || _pressedSignature == null)
            return false;

        var pressed = _pressedSignature;
        _pressedSignature = null;
        args.Handle();
        if (GetSignatureButton(args.RelativePosition) is { } button && button.Entity == pressed)
            ToggleSignaturePopup(button);

        return true;
    }

    private SignatureButton? GetSignatureButton(Vector2 position)
    {
        if (!ShowIFF)
            return null;

        for (var i = _signatureButtons.Count - 1; i >= 0; i--)
        {
            if (_signatureButtons[i].Bounds.Contains(position * UIScale))
                return _signatureButtons[i];
        }
        return null;
    }

    private void ToggleSignaturePopup(SignatureButton button)
    {
        var close = _signaturePopup != null && _openSignature == button.Entity;
        _signaturePopup?.Close();
        if (close)
            return;

        _openSignature = button.Entity;
        var popup = new Popup { MouseFilter = MouseFilterMode.Pass, CloseOnClick = false };
        var label = new Label();
        var scroll = new ScrollContainer
        {
            MinWidth = 110,
            MaxWidth = MathF.Max(110, Width - 24),
            MaxHeight = Math.Clamp(Height / 2, 60, 220),
            ReturnMeasure = true,
        };
        scroll.AddChild(label);
        var panel = new PanelContainer
        {
            MouseFilter = MouseFilterMode.Stop,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = SignatureButtonColor,
                BorderColor = SignatureBorderColor,
                BorderThickness = new Thickness(1),
                ContentMarginLeftOverride = 7,
                ContentMarginRightOverride = 7,
                ContentMarginTopOverride = 4,
                ContentMarginBottomOverride = 4,
            },
        };
        panel.AddChild(scroll);
        popup.AddChild(panel);
        popup.OnPopupHide += () =>
        {
            popup.Orphan();
            _signaturePopup = null;
            _signaturePopupLabel = null;
        };

        _signaturePopup = popup;
        _signaturePopupLabel = label;
        UpdateSignaturePopup();
        var origin = GlobalPosition + new Vector2(button.Bounds.Left, button.Bounds.Bottom) / UIScale;
        var above = GlobalPosition + button.Bounds.TopLeft / UIScale;
        UserInterfaceManager.GetRootForMouse().ModalRoot.AddChild(popup);
        popup.Open(UIBox2.FromDimensions(origin, Vector2.Zero), altPosUp: above);
    }

    private void UpdateSignaturePopup()
    {
        if (_signaturePopupLabel == null)
            return;

        foreach (var button in _signatureButtons)
        {
            if (button.Entity != _openSignature)
                continue;

            _popupNames.Clear();
            if (button.Entity == EntityUid.Invalid)
                _popupNames.AddRange(_offscreenSignatures);
            else
            {
                for (var i = button.Start; i < button.Start + button.Count; i++)
                    _popupNames.Add(_crowdedSignatures[_signatureOrder[i]].Name);
            }

            _popupNames.Sort(StringComparer.CurrentCulture);
            var names = string.Join('\n', _popupNames);
            if (_signaturePopupLabel.Text != names)
                _signaturePopupLabel.Text = names;
            return;
        }

        _signaturePopup?.Close();
    }

    protected override void ExitedTree()
    {
        _signaturePopup?.Close();
        _pressedSignature = null;
        base.ExitedTree();
    }

    protected override void VisibilityChanged(bool newVisible)
    {
        if (!VisibleInTree)
        {
            _signaturePopup?.Close();
            _pressedSignature = null;
        }
        base.VisibilityChanged(newVisible);
    }
}

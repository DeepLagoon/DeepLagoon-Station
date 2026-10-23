using System.Linq;
using Content.Client.IconSmoothing;
using Content.Shared._DeepLagoon.Mapping;
using Robust.Client.GameObjects;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>Static IconSmooth rendering over plan neighbours; no entities or gameplay systems are started.</summary>
public sealed class TguiWallImages
{
    private readonly IPrototypeManager _prototypes;
    private readonly TguiSpriteImages _images;
    private readonly Dictionary<(int X, int Y), List<GridEditorItem>> _cells;

    public TguiWallImages(TguiSpriteImages images, IEnumerable<GridEditorItem> items, IPrototypeManager prototypes)
    {
        _images = images; _prototypes = prototypes;
        _cells = items.Where(item => item.Kind == "entity").GroupBy(item => Cell(item.X, item.Y)).ToDictionary(group => group.Key, group => group.ToList());
    }

    private static (int X, int Y) Cell(float x, float y) => ((int) MathF.Floor(x), (int) MathF.Floor(y));

    public IEnumerable<TguiData> Images(GridEditorItem item)
    {
        var proto = _prototypes.Index<EntityPrototype>(item.Prototype);
        if (!proto.TryGetComponent<IconSmoothComponent>(out var smooth) || !smooth.Enabled || smooth.Mode == IconSmoothingMode.NoSprite ||
            !proto.TryGetComponent<SpriteComponent>(out var sprite) || sprite.BaseRSI is not { } rsi)
            return _images.MapItem(item.Prototype);

        bool Match(int dx, int dy)
        {
            var cell = Cell(item.X, item.Y);
            if (!_cells.TryGetValue((cell.X + dx, cell.Y + dy), out var candidates)) return false;
            return candidates.Any(other =>
            {
                if (!_prototypes.TryIndex<EntityPrototype>(other.Prototype, out var target) ||
                    !target.TryGetComponent<IconSmoothComponent>(out var connected) || !connected.Enabled || connected.SmoothKey != smooth.SmoothKey) return false;
                if (connected.Mode is not (IconSmoothingMode.Diagonal or IconSmoothingMode.DiagonalNF)) return true;
                var angle = -other.Rotation * MathF.PI / 180;
                var x = -dx * MathF.Cos(angle) + dy * MathF.Sin(angle);
                var y = -dx * MathF.Sin(angle) - dy * MathF.Cos(angle);
                return x >= -.01f && y <= .01f;
            });
        }

        TguiData Frame(int state, RsiDirection direction) => new TguiData()
            .String("url", _images.DirectionalFrame(rsi.Path, smooth.StateBase + state, direction)).String("state", smooth.StateBase + state)
            .Bool("worldAligned", smooth.Mode == IconSmoothingMode.Corners)
            .Number("direction", (int) direction).String("color", sprite.Color.ToHexNoAlpha()).Bool("native", true)
            .Number("width", rsi.Size.X).Number("height", rsi.Size.Y)
            .Number("m11", sprite.LocalMatrix.M11).Number("m12", sprite.LocalMatrix.M12).Number("m21", sprite.LocalMatrix.M21)
            .Number("m22", sprite.LocalMatrix.M22).Number("m31", sprite.LocalMatrix.M31).Number("m32", sprite.LocalMatrix.M32);

        if (smooth.Mode == IconSmoothingMode.Corners)
        {
            // Baystation corner bits and RSI directions, as in IconSmoothSystem.
            var n = Match(0, 1); var e = Match(1, 0); var s = Match(0, -1); var w = Match(-1, 0);
            int Fill(bool ccw, bool diagonal, bool cw) => (ccw ? 1 : 0) | (diagonal ? 2 : 0) | (cw ? 4 : 0);
            var result = new List<TguiData>();
            if (sprite.LayerData.Count > 0) result.AddRange(_images.MapItem(item.Prototype));
            result.Add(Frame(Fill(e, Match(1, -1), s), RsiDirection.South));
            result.Add(Frame(Fill(n, Match(1, 1), e), RsiDirection.East));
            result.Add(Frame(Fill(w, Match(-1, 1), n), RsiDirection.North));
            result.Add(Frame(Fill(s, Match(-1, -1), w), RsiDirection.West));
            return result;
        }
        if (smooth.Mode == IconSmoothingMode.CardinalFlags)
            return [Frame((Match(0, 1) ? 1 : 0) | (Match(0, -1) ? 2 : 0) | (Match(1, 0) ? 4 : 0) | (Match(-1, 0) ? 8 : 0), RsiDirection.South)];

        bool LocalMatch(int x, int y)
        {
            var angle = item.Rotation * MathF.PI / 180;
            return Match((int) MathF.Round(x * MathF.Cos(angle) - y * MathF.Sin(angle)), (int) MathF.Round(x * MathF.Sin(angle) + y * MathF.Cos(angle)));
        }
        var value = (LocalMatch(1, 0) ? 1 : 0) | (LocalMatch(0, -1) ? 2 : 0);
        if (smooth.Mode == IconSmoothingMode.Diagonal) value = value == 3 && LocalMatch(1, -1) ? 1 : 0;
        else if (value == 3 && LocalMatch(1, -1)) value = 4;
        return [Frame(value, RsiDirection.South)];
    }
}


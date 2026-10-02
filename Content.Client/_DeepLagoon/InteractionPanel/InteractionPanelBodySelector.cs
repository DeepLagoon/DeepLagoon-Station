using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Input;

namespace Content.Client._DeepLagoon.InteractionPanel;

// Условная кукла отдельно от SpriteView: область наведения не зависит от одежды и размеров моба.
public sealed class InteractionPanelBodySelector : Control
{
    public static readonly string[] Regions = ["all", "head", "hands", "shoulders", "torso", "groin", "legs"];
    // Альфа-контур южного кадра Mobs/Species/Human/parts.rsi/full.png (32x32).
    // Сохраняем пиксельный силуэт MobHuman, без одежды и зависимости от живой сущности.
    // Зоны определяются внутри маски: пустота между конечностями не кликабельна.
    private static readonly string[] Silhouette =
    [
        "................................",
        "................................",
        "................................",
        "..............XXX...............",
        ".............XXXXX..............",
        "............XXXXXXX.............",
        "...........XXXXXXXXX............",
        "...........XXXXXXXXX............",
        "............XXXXXXX.............",
        ".............XXXXX..............",
        "...........XXXXXXXXX............",
        "..........XXXXXXXXXXX...........",
        ".........XXXXXXXXXXXXX..........",
        "........XXXXXXXXXXXXXXX.........",
        "........XXXXXXXXXXXXXXX.........",
        ".......XXXXXXXXXXXXXXXXX........",
        ".......XXX.XXXXXXXXX.XXX........",
        ".......XXX.XXXXXXXXX.XXX........",
        ".......XXXXXXXXXXXXXXXXX........",
        ".......XXXXXXXXXXXXXXXXX........",
        ".......XXXXXXXXXXXXXXXXX........",
        "........XX.XXXXXXXXX.XX.........",
        "...........XXXXXXXXX............",
        "...........XXXXXXXXX............",
        "...........XXXX.XXXX............",
        "...........XXXX.XXXX............",
        "...........XXXX.XXXX............",
        "...........XXXX.XXXX............",
        "...........XXXX.XXXX............",
        "..........XXXXX.XXXXX...........",
        ".........XXXXXX.XXXXXX..........",
        ".........XXXXXX.XXXXXX..........",
    ];

    private static string? RegionAt(int x, int y)
    {
        if (x < 0 || x >= 32 || y < 0 || y >= 32 || Silhouette[y][x] != 'X') return null;
        if (y <= 9) return "head";
        if (y <= 12) return "shoulders";
        if (y <= 21 && (x <= 10 || x >= 20)) return "hands";
        if (y <= 20) return "torso";
        return y <= 23 ? "groin" : "legs";
    }

    private float BodyScale => Math.Min(Width / 32, Height / 34);
    private Vector2 BodyOffset => new((Width - 32 * BodyScale) / 2, (Height - 32 * BodyScale) / 2);
    public string Selected { get; set; } = "all";
    private string? _hover;
    public event Action<string>? RegionSelected;
    public event Action<string?>? RegionHovered;

    public InteractionPanelBodySelector()
    {
        MinSize = new Vector2(180, 184);
        MouseFilter = MouseFilterMode.Stop;
    }

    private string? Hit(Vector2 point)
    {
        if (BodyScale <= 0) return null;
        point = (point - BodyOffset) / BodyScale;
        return RegionAt((int) MathF.Floor(point.X), (int) MathF.Floor(point.Y));
    }
    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);
        _hover = Hit(args.RelativePosition);
        ToolTip = _hover == null ? null : Loc.GetString($"dl-interaction-panel-body-{_hover}");
        RegionHovered?.Invoke(_hover);
    }

    protected override void MouseExited()
    {
        base.MouseExited();
        _hover = null;
        RegionHovered?.Invoke(null);
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);
        if (args.Function != EngineKeyFunctions.UIClick || Hit(args.RelativePosition) is not { } body) return;
        Selected = Selected == body ? "all" : body;
        RegionSelected?.Invoke(Selected);
        args.Handle();
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        var scale = BodyScale * UIScale;
        var offset = BodyOffset * UIScale;
        for (var y = 0; y < 32; y++)
        {
            // Объединяем соседние пиксели зоны в полосу, без зазоров между частями тела.
            for (var x = 0; x < 32;)
            {
                var body = RegionAt(x, y);
                var end = x + 1;
                while (end < 32 && RegionAt(end, y) == body) end++;
                if (body != null)
                {
                    var color = body == _hover ? InteractionPanelAppearance.BodyHover : body == Selected
                        ? InteractionPanelAppearance.BodySelected : body switch
                        {
                            "head" => InteractionPanelAppearance.BodyLight,
                            "shoulders" => InteractionPanelAppearance.BodyDark,
                            "torso" => InteractionPanelAppearance.BodyLight,
                            "hands" => InteractionPanelAppearance.BodyDark,
                            "groin" => InteractionPanelAppearance.BodyPelvis,
                            _ => InteractionPanelAppearance.BodyLight,
                        };
                    handle.DrawRect(new UIBox2(offset.X + x * scale, offset.Y + y * scale,
                        offset.X + end * scale, offset.Y + (y + 1) * scale), color);
                }
                x = end;
            }
        }
    }
}


using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.Server._DeepLagoon.Mapping;

/// <summary>
/// makegrid &lt;ширина&gt; &lt;высота&gt;: новый грид, целиком покрытый обшивкой, с центром на исполнителе.
/// Нечётная сторона: исполнитель над центральным тайлом. Чётная: центр на границе двух центральных
/// тайлов, ближайшей к исполнителю (округление .5 вверх). Грид стоит в целой точке мира с поворотом 0,
/// индексы тайлов идут от -(размер / 2) (целочисленное деление), поэтому начало координат грида
/// лежит в его центре (для нечётной стороны в углу центрального тайла).
/// </summary>
[AdminCommand(AdminFlags.Mapping)]
public sealed partial class MakeGridCommand : LocalizedEntityCommands
{
    /// <summary>Максимум тайлов по каждой стороне.</summary>
    public const int MaxSide = 256;

    private const string PlatingTileId = "Plating";

    // Числа в сообщения идут строками: Fluent форматирует int по культуре ("1 024").
    private static readonly string MaxText = MaxSide.ToString(CultureInfo.InvariantCulture);

    [Dependency] private IMapManager _mapManager = default!;
    [Dependency] private ITileDefinitionManager _tileDefs = default!;
    [Dependency] private ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override string Command => "makegrid";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError(Loc.GetString("cmd-makegrid-wrong-args"));
            shell.WriteLine(Help);
            return;
        }

        if (!TryParseSide(args[0], out var width) || !TryParseSide(args[1], out var height))
        {
            shell.WriteError(Loc.GetString("cmd-makegrid-bad-size", ("max", MaxText)));
            shell.WriteLine(Help);
            return;
        }

        if (shell.Player is not { } player)
        {
            shell.WriteError(Loc.GetString("cmd-makegrid-no-player"));
            return;
        }

        if (player.AttachedEntity is not { } executor || !EntityManager.EntityExists(executor))
        {
            shell.WriteError(Loc.GetString("cmd-makegrid-no-entity"));
            return;
        }

        var coords = _transform.GetMapCoordinates(executor);
        if (coords.MapId == MapId.Nullspace || !_map.MapExists(coords.MapId))
        {
            shell.WriteError(Loc.GetString("cmd-makegrid-no-map"));
            return;
        }

        if (!_tileDefs.TryGetDefinition(PlatingTileId, out var plating))
        {
            shell.WriteError(Loc.GetString("cmd-makegrid-no-tile", ("tile", PlatingTileId)));
            return;
        }

        // Начало грида: целая точка мира, чтобы тайлы совпали с сеткой мира.
        var origin = new Vector2i(OriginCoordinate(coords.Position.X, width), OriginCoordinate(coords.Position.Y, height));
        var first = new Vector2i(-(width / 2), -(height / 2));
        var area = Box2.FromDimensions(origin.X + first.X, origin.Y + first.Y, width, height);

        if (OverlapsExistingTiles(coords.MapId, area))
        {
            shell.WriteError(Loc.GetString("cmd-makegrid-overlap"));
            return;
        }

        var tile = new Tile(plating.TileId);
        var tiles = new List<(Vector2i, Tile)>(width * height);
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                tiles.Add((new Vector2i(first.X + x, first.Y + y), tile));
            }
        }

        var grid = _mapManager.CreateGridEntity(coords.MapId);
        try
        {
            _transform.SetWorldPosition(grid.Owner, new Vector2(origin.X, origin.Y));
            _map.SetTiles(grid, tiles);
        }
        catch
        {
            EntityManager.DeleteEntity(grid.Owner);
            throw;
        }

        _adminLogger.Add(LogType.AdminCommands, LogImpact.High,
            $"{EntityManager.ToPrettyString(executor):actor} created grid {EntityManager.ToPrettyString(grid.Owner):entity} {width}x{height} at map {coords.MapId} ({origin.X}, {origin.Y})");

        shell.WriteLine(Loc.GetString("cmd-makegrid-done",
            ("grid", EntityManager.GetNetEntity(grid.Owner).ToString()),
            ("width", width.ToString(CultureInfo.InvariantCulture)),
            ("height", height.ToString(CultureInfo.InvariantCulture)),
            ("x", origin.X.ToString(CultureInfo.InvariantCulture)),
            ("y", origin.Y.ToString(CultureInfo.InvariantCulture))));
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHint(Loc.GetString("cmd-makegrid-hint-width", ("max", MaxText))),
            2 => CompletionResult.FromHint(Loc.GetString("cmd-makegrid-hint-height", ("max", MaxText))),
            _ => CompletionResult.Empty,
        };
    }

    private static bool TryParseSide(string arg, out int side)
    {
        return int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out side)
               && side >= 1
               && side <= MaxSide;
    }

    /// <summary>
    /// Нечётная сторона: тайл под исполнителем, его угол и есть начало. Чётная: ближайшая к исполнителю линия сетки.
    /// </summary>
    private static int OriginCoordinate(float position, int side)
    {
        return side % 2 == 1
            ? (int) MathF.Floor(position)
            : (int) MathF.Floor(position + 0.5f);
    }

    /// <summary>
    /// Есть ли у другого грида на карте непустой тайл внутри прямоугольника. Гриды ищутся в дереве гридов карты
    /// (оно обновляется при смене тайлов и не зависит от тика физики и от паузы карты). Дальше проверяются только
    /// тайлы в пределах границ найденного грида, с выходом на первом непустом. Сама карта как грид не считается
    /// (земля планеты не мешает ставить грид, как и шаттлу).
    /// </summary>
    private bool OverlapsExistingTiles(MapId mapId, Box2 area)
    {
        var candidates = new List<Entity<MapGridComponent>>();
        _mapManager.FindGridsIntersecting(mapId, area, ref candidates, approx: true, includeMap: false);

        foreach (var (uid, grid) in candidates)
        {
            var local = _transform.GetInvWorldMatrix(uid).TransformBox(area);
            if (!local.Intersects(grid.LocalAABB))
                continue;

            var enumerator = _map.GetLocalTilesEnumerator(uid, grid, local.Intersect(grid.LocalAABB));
            if (enumerator.MoveNext(out _))
                return true;
        }

        return false;
    }
}

using Content.Shared.Atmos;
using Robust.Shared.Maths;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Mapping;

[Serializable, NetSerializable]
public enum GridEditorAirStatus : byte { Blocked, Sealed, Leaking, Space }

[Serializable, NetSerializable]
public sealed class GridEditorAtmosCell
{
    public int X, Y, Region;
    public AtmosDirection Blocked, Leaks;
    public bool MapAtmosphere, NoAirWhenBlocked;
    public float Pressure;
    public GridEditorAirStatus Status;
}

[Serializable, NetSerializable]
public sealed class GridEditorAtmosRequest(Guid token, bool fill = false) : EntityEventArgs
{
    public readonly Guid Token = token;
    public readonly bool Fill = fill;
}

[Serializable, NetSerializable]
public sealed class GridEditorAtmosResult(Guid token, List<GridEditorAtmosCell> cells, int filled) : EntityEventArgs
{
    public readonly Guid Token = token;
    public readonly List<GridEditorAtmosCell> Cells = cells;
    public readonly int Filled = filled;
}

/// <summary>Flood fill using the same two-sided cardinal blocking rules as atmos adjacency.</summary>
public static class GridEditorAtmosTopology
{
    public static void Analyze(Dictionary<Vector2i, GridEditorAtmosCell> cells)
    {
        foreach (var cell in cells.Values)
        {
            cell.Region = 0; cell.Leaks = AtmosDirection.Invalid;
            cell.Status = cell.MapAtmosphere ? GridEditorAirStatus.Space :
                cell.Blocked == AtmosDirection.All && cell.NoAirWhenBlocked ? GridEditorAirStatus.Blocked : GridEditorAirStatus.Sealed;
        }
        var region = 0;
        foreach (var start in cells.Values)
        {
            if (start.Region != 0 || start.Status is GridEditorAirStatus.Blocked or GridEditorAirStatus.Space) continue;
            region++;
            var members = new List<GridEditorAtmosCell>();
            var queue = new Queue<GridEditorAtmosCell>();
            start.Region = region; queue.Enqueue(start);
            var leaking = false;
            while (queue.TryDequeue(out var cell))
            {
                members.Add(cell);
                for (var i = 0; i < Atmospherics.Directions; i++)
                {
                    var direction = (AtmosDirection) (1 << i);
                    if (cell.Blocked.IsFlagSet(direction)) continue;
                    var position = new Vector2i(cell.X, cell.Y).Offset(direction);
                    cells.TryGetValue(position, out var neighbor);
                    if (neighbor != null && neighbor.Blocked.IsFlagSet(direction.GetOpposite())) continue;
                    if (neighbor == null || neighbor.MapAtmosphere)
                    {
                        cell.Leaks |= direction; leaking = true; continue;
                    }
                    if (neighbor.Status == GridEditorAirStatus.Blocked || neighbor.Region != 0) continue;
                    neighbor.Region = region; queue.Enqueue(neighbor);
                }
            }
            foreach (var cell in members) cell.Status = leaking ? GridEditorAirStatus.Leaking : GridEditorAirStatus.Sealed;
        }
    }
}

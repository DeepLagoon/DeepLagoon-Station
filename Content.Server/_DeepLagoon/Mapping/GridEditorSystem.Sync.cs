using System.Linq;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Administration;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{
    private float _syncElapsed;

    // Capture once per active grid. Each viewer receives only its own baseline delta;
    // catalog queries, favorites and all other client preferences are never broadcast.
    private void SynchronizeGrid(EntityUid uid, ICommonSession? appliedBy = null)
    {
        if (!TryComp<MapGridComponent>(uid, out var grid)) return;
        var viewers = _drafts.Where(pair => pair.Value.Grid == uid && _admins.HasAdminFlag(pair.Key, AdminFlags.Mapping)).ToArray();
        if (viewers.Length == 0) return;
        var source = viewers[0].Value;
        var items = Capture(source, (_, _) => true);
        var entities = new Dictionary<string, EntityUid>(source.Entities);
        var after = items.ToDictionary(item => item.Id);
        var bounds = grid.LocalAABB;
        var gridX = (int) MathF.Floor(bounds.Left / grid.TileSize);
        var gridY = (int) MathF.Floor(bounds.Bottom / grid.TileSize);
        var gridRight = (int) MathF.Ceiling(bounds.Right / grid.TileSize);
        var gridTop = (int) MathF.Ceiling(bounds.Top / grid.TileSize);
        var minX = gridX; var minY = gridY;
        var maxX = gridRight; var maxY = gridTop;
        foreach (var item in items)
        {
            minX = Math.Min(minX, (int) MathF.Floor(item.X));
            minY = Math.Min(minY, (int) MathF.Floor(item.Y));
            maxX = Math.Max(maxX, (int) MathF.Floor(item.X) + 1);
            maxY = Math.Max(maxY, (int) MathF.Floor(item.Y) + 1);
        }
        // The applied empty editing extent is shared; each viewer keeps its own camera.
        foreach (var (_, draft) in viewers)
        {
            minX = Math.Min(minX, draft.X); minY = Math.Min(minY, draft.Y);
            maxX = Math.Max(maxX, draft.X + draft.Width); maxY = Math.Max(maxY, draft.Y + draft.Height);
        }
        foreach (var (player, draft) in viewers)
        {
            var previous = draft.Items.ToDictionary(item => item.Id);
            var changed = items.Where(item => !previous.TryGetValue(item.Id, out var old) || !item.Same(old)).ToList();
            var removed = previous.Keys.Where(id => !after.ContainsKey(id)).ToList();
            var left = Math.Min(draft.X, minX); var bottom = Math.Min(draft.Y, minY);
            var right = Math.Max(draft.X + draft.Width, maxX); var top = Math.Max(draft.Y + draft.Height, maxY);
            var resized = draft.X != left || draft.Y != bottom || draft.Width != right - left || draft.Height != top - bottom ||
                draft.GridX != gridX || draft.GridY != gridY || draft.GridWidth != gridRight - gridX || draft.GridHeight != gridTop - gridY;
            draft.Items = items;
            draft.Entities.Clear();
            foreach (var (id, entity) in entities)
            { draft.Entities[id] = entity; draft.KnownEntities[id] = entity; }
            foreach (var item in items) draft.KnownItems.TryAdd(item.Id, item);
            draft.X = left; draft.Y = bottom; draft.Width = right - left; draft.Height = top - bottom;
            draft.GridX = gridX; draft.GridY = gridY; draft.GridWidth = gridRight - gridX; draft.GridHeight = gridTop - gridY;
            var applied = player == appliedBy;
            if (changed.Count == 0 && removed.Count == 0 && !resized && !applied) continue;
            RaiseNetworkEvent(new GridEditorPatchEvent(draft.Token, changed, removed, left, bottom, right - left, top - bottom,
                gridX, gridY, gridRight - gridX, gridTop - gridY, applied, draft.PreviewGeneration), player);
        }
    }
}

using System.Linq;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Administration;
using Content.Shared.Decals;
using Content.Shared.Maps;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{
    private int _previewRevision;

    private void CloseEditor(ICommonSession player, Guid? token = null)
    {
        foreach (var (key, draft) in _drafts.Where(pair => pair.Key.Player == player && (token == null || pair.Key.Token == token)).ToArray())
        {
            _drafts.Remove(key);
            RaiseNetworkEvent(new GridEditorClosedEvent(draft.Token), player);
            PublishPreviews(draft.Grid);
        }
    }

    private void Preview(GridEditorDraftUpdateEvent ev, EntitySessionEventArgs args)
    {
        if (!_admins.HasAdminFlag(args.SenderSession, AdminFlags.Mapping) ||
            !_drafts.TryGetValue((args.SenderSession, ev.Token), out var draft) || draft.Token != ev.Token ||
            draft.PreviewGeneration != ev.Generation || !Exists(draft.Grid)) return;
        var expansion = ev.Expansion ?? new GridEditorExpansion();
        if ((long) draft.Width + expansion.Left + expansion.Right < 1 ||
            (long) draft.Height + expansion.Bottom + expansion.Top < 1 ||
            (long) draft.X - expansion.Left < int.MinValue || (long) draft.Y - expansion.Bottom < int.MinValue ||
            (long) draft.X + draft.Width + expansion.Right > int.MaxValue ||
            (long) draft.Y + draft.Height + expansion.Top > int.MaxValue ||
            (long) draft.Width + expansion.Left + expansion.Right > int.MaxValue ||
            (long) draft.Height + expansion.Bottom + expansion.Top > int.MaxValue) return;
        var seen = new HashSet<string>();
        foreach (var item in ev.Changes)
        {
            if (item.Id.Length is < 1 or > 80 || !seen.Add(item.Id)) return;
            if (item.Kind is "clear" or "remove") continue;
            if (!float.IsFinite(item.X) || !float.IsFinite(item.Y) || !float.IsFinite(item.Rotation) ||
                item.X < int.MinValue || item.X >= int.MaxValue || item.Y < int.MinValue || item.Y >= int.MaxValue || Math.Abs(item.Rotation) > 3600 ||
                item.Prototype.Length > 200 || item.Color.Length > 9 || !Color.TryFromHex(item.Color, out _)) return;
            var valid = item.Kind switch
            {
                "entity" => _prototypes.TryIndex<EntityPrototype>(item.Prototype, out var entity) && !entity.Abstract,
                "decal" => _prototypes.HasIndex<DecalPrototype>(item.Prototype),
                "tile" => _tiles.TryGetDefinition(item.Prototype, out var tile) && tile is ContentTileDefinition definition &&
                    !definition.Abstract && tile.TileId != 0 && item.Variant < definition.Variants && item.Mirror <= 7,
                _ => false
            };
            if (!valid) return;
        }
        SupersedePreviews(draft.Grid, args.SenderSession, ev.Changes.Where(item => item.Kind != "clear").Select(item => item.Id).ToHashSet());
        foreach (var item in ev.Changes)
            if (item.Kind == "clear") draft.Preview.Remove(item.Id); else draft.Preview[item.Id] = item;
        draft.PreviewExpansion = expansion;
        draft.PreviewRevision = ++_previewRevision;
        PublishPreviews(draft.Grid);
    }

    // Editing the same item transfers its draft intent to the latest mapper. Notify
    // the previous author so their local overlay cannot hide the new edit or respawn
    // an already-applied temporary ID. A generation change rejects in-flight previews.
    private void SupersedePreviews(EntityUid grid, ICommonSession author, HashSet<string> ids)
    {
        foreach (var ((player, _), draft) in _drafts)
        {
            if (player == author || draft.Grid != grid) continue;
            var superseded = draft.Preview.Values.Where(item => ids.Contains(item.Id)).ToList();
            if (superseded.Count == 0) continue;
            foreach (var item in superseded) draft.Preview.Remove(item.Id);
            draft.PreviewGeneration++;
            RaiseNetworkEvent(new GridEditorPatchEvent(draft.Token, new(), new(), draft.X, draft.Y, draft.Width, draft.Height,
                draft.GridX, draft.GridY, draft.GridWidth, draft.GridHeight, previewGeneration: draft.PreviewGeneration,
                superseded: superseded), player);
        }
    }

    // Unsaved changes are shared editing input, while the world changes only on Apply.
    private void PublishPreviews(EntityUid grid)
    {
        var viewers = _drafts.Where(pair => pair.Value.Grid == grid && _admins.HasAdminFlag(pair.Key.Player, AdminFlags.Mapping))
            .OrderBy(pair => pair.Value.PreviewRevision).ToArray();
        foreach (var ((player, _), draft) in viewers)
        {
            var peers = new List<GridEditorPeerDraft>();
            foreach (var (_, peer) in viewers)
            foreach (var item in peer.Preview.Values.Where(item => item.Kind != "remove"))
                draft.KnownItems[item.Id] = item;
            foreach (var ((other, _), peer) in viewers)
            {
                if (other == player) continue;
                var removed = new List<GridEditorItem>();
                foreach (var item in peer.Preview.Values.Where(i => i.Kind == "remove"))
                    if (peer.Items.FirstOrDefault(current => current.Id == item.Id) is { } current) removed.Add(current);
                    else if (peer.KnownItems.TryGetValue(item.Id, out var old)) removed.Add(old);
                peers.Add(new GridEditorPeerDraft { Player = other.UserId.ToString(), Name = other.Name,
                    Expansion = peer.PreviewExpansion, Items = peer.Preview.Values.Where(item => item.Kind != "remove").ToList(), RemovedItems = removed });
            }
            RaiseNetworkEvent(new GridEditorDraftsEvent(draft.Token, peers), player);
        }
    }
}

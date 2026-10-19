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

    private void CloseEditor(ICommonSession player)
    {
        if (!_drafts.Remove(player, out var draft)) return;
        PublishPreviews(draft.Grid);
    }

    private void Preview(GridEditorDraftUpdateEvent ev, EntitySessionEventArgs args)
    {
        if (!_admins.HasAdminFlag(args.SenderSession, AdminFlags.Mapping) ||
            !_drafts.TryGetValue(args.SenderSession, out var draft) || draft.Token != ev.Token ||
            draft.PreviewGeneration != ev.Generation || !Exists(draft.Grid)) return;
        var expansion = ev.Expansion ?? new GridEditorExpansion();
        if (expansion.Left < 0 || expansion.Right < 0 || expansion.Bottom < 0 || expansion.Top < 0 ||
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
        foreach (var item in ev.Changes)
            if (item.Kind == "clear") draft.Preview.Remove(item.Id); else draft.Preview[item.Id] = item;
        draft.PreviewExpansion = expansion;
        draft.PreviewRevision = ++_previewRevision;
        PublishPreviews(draft.Grid);
    }

    // Foreign drafts are previews only. They never write to the world and are never
    // included in another mapper's Apply. Local edits are drawn above foreign previews.
    private void PublishPreviews(EntityUid grid)
    {
        var viewers = _drafts.Where(pair => pair.Value.Grid == grid && _admins.HasAdminFlag(pair.Key, AdminFlags.Mapping))
            .OrderBy(pair => pair.Value.PreviewRevision).ToArray();
        foreach (var (player, draft) in viewers)
        {
            var peers = new List<GridEditorPeerDraft>();
            foreach (var (other, peer) in viewers)
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

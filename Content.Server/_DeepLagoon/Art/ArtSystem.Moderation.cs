using Content.Shared._DeepLagoon.Art;

namespace Content.Server._DeepLagoon.Art;

public sealed partial class ArtSystem
{
    private readonly HashSet<(Guid Work, string Hash)> _removedArt = new();

    public void RemoveArchivedArtwork(Guid id, Guid work, string hash)
    {
        _removedArt.Add((work, hash));
        _pool?.Remove(id);
        _available.RemoveAll(a => a == id);
        var canvases = EntityQueryEnumerator<ArtCanvasComponent>();
        while (canvases.MoveNext(out var uid, out var canvas))
        {
            if (canvas.WorkId != work || canvas.RevisionHash != hash) continue;
            ClearRemovedCanvas(uid, canvas);
        }
    }

    private void ClearRemovedCanvas(EntityUid uid, ArtCanvasComponent canvas)
    {
        _ui.CloseUis(uid);
        Release(uid, canvas);
        canvas.Document = ArtLimits.EmptyDocument;
        canvas.SavedPreview = "";
        canvas.Title = "";
        canvas.RevisionHash = "";
        canvas.WorkId = Guid.NewGuid();
        canvas.Author = Guid.Empty;
        canvas.AuthorName = "";
        canvas.Revision++;
        SetPreview(uid, "");
        if (_containers.TryGetContainingContainer(uid, out var container) && HasComp<ArtFrameComponent>(container.Owner))
            SetPreview(container.Owner, "");
    }
}

using System.Text;
using System.Threading.Tasks;
using Content.Shared._DeepLagoon.Art;

namespace Content.Server._DeepLagoon.Art;

public sealed partial class ArtSystem
{
    private sealed class DraftUpload(ArtEditorMessage message, TimeSpan expires)
    {
        public readonly int Count = message.Count;
        public readonly string Writer = message.Writer;
        public readonly int Sequence = message.Sequence;
        public readonly int Epoch = message.Epoch;
        public readonly int Version = message.Version;
        public readonly TimeSpan Expires = expires;
        public readonly StringBuilder Data = new();
        public int Next;
    }
    private readonly Dictionary<EntityUid, DraftUpload> _draftUploads = new();
    private readonly Dictionary<EntityUid, TimeSpan> _draftAt = new();
    private readonly HashSet<EntityUid> _draftBaking = new();
    private int _draftJobs;
    private static string CurrentDocument(ArtCanvasComponent c) => c.DraftDocument.Length == 0 ? c.Document : c.DraftDocument;
    private static string CurrentPreview(ArtCanvasComponent c) => c.DraftDocument.Length == 0 ? c.SavedPreview : c.DraftPreview;

    private void ClearDraft(EntityUid uid, ArtCanvasComponent c)
    {
        c.DraftDocument = ""; c.DraftPreview = ""; c.DraftWriter = ""; c.DraftSequence = 0;
        c.DraftEpoch++; c.DraftVersion++; c.DraftLeaseGeneration++;
        _draftUploads.Remove(uid);
        if (_tabletLeases.TryGetValue(uid, out var lease)) lease.DraftChanged = false;
    }

    private void StoreDraft(EntityUid uid, ArtCanvasComponent c, ArtDocument.Baked baked, string writer, int sequence)
    {
        c.DraftDocument = baked.Json; c.DraftPreview = baked.Preview;
        c.DraftWriter = writer; c.DraftSequence = sequence; c.DraftVersion++;
        SetPreview(uid, c.DraftPreview);
        if (_tabletLeases.TryGetValue(uid, out var lease))
        { lease.Document = c.DraftDocument; lease.DraftChanged = true; }
    }

    private ArtDraftStatusMessage DraftStatus(EntityUid uid, ArtCanvasComponent c, ArtEditorMessage ev, bool success)
    {
        var result = new ArtDraftStatusMessage(ev.Writer, ev.Sequence, c.DraftEpoch, c.DraftVersion, success);
        _ui.ServerSendUiMessage(uid, ArtUiKey.Key, result, ev.Actor);
        return result;
    }

    private async Task OnDraftChunk(EntityUid uid, ArtCanvasComponent c, ArtEditorMessage ev)
    {
        if (ev.Text.Length > ArtLimits.ChunkChars || ev.Count < 1 || ev.Count > (ArtLimits.MaxDocumentChars + ArtLimits.ChunkChars - 1) / ArtLimits.ChunkChars
            || ev.Index < 0 || ev.Index >= ev.Count || !Guid.TryParseExact(ev.Writer, "N", out _) || ev.Sequence < 1 || ev.Epoch != c.DraftEpoch)
        { DraftStatus(uid, c, ev, false); return; }
        if (ev.Index == 0)
        {
            if (_draftAt.GetValueOrDefault(uid) > _timing.CurTime || _baking.Contains(uid) || _draftBaking.Contains(uid))
            { DraftStatus(uid, c, ev, false); return; }
            _draftAt[uid] = _timing.CurTime + TimeSpan.FromSeconds(1);
            _draftUploads[uid] = new DraftUpload(ev, _timing.CurTime + TimeSpan.FromMinutes(2));
        }
        if (!_draftUploads.TryGetValue(uid, out var upload) || upload.Count != ev.Count || upload.Writer != ev.Writer || upload.Sequence != ev.Sequence
            || upload.Epoch != ev.Epoch || upload.Version != ev.Version || upload.Next != ev.Index || upload.Expires < _timing.CurTime
            || upload.Data.Length + ev.Text.Length > ArtLimits.MaxDocumentChars)
        { _draftUploads.Remove(uid); DraftStatus(uid, c, ev, false); return; }
        upload.Data.Append(ev.Text); upload.Next++;
        if (upload.Next != upload.Count) return;
        _draftUploads.Remove(uid);
        await BakeDraft(uid, c, ev, upload.Data.ToString());
    }

    // Authorization is checked before decoding. A complete, already accepted update may finish after UI closure.
    private async Task<ArtDraftStatusMessage> BakeDraft(EntityUid uid, ArtCanvasComponent c, ArtEditorMessage ev, string document)
    {
        if (ev.Epoch != c.DraftEpoch || !Guid.TryParseExact(ev.Writer, "N", out _) || ev.Sequence < 1 || document.Length > ArtLimits.MaxDocumentChars)
            return DraftStatus(uid, c, ev, false);
        if (ev.Writer == c.DraftWriter && ev.Sequence <= c.DraftSequence)
            return DraftStatus(uid, c, ev, true);
        if ((ev.Writer != c.DraftWriter && ev.Version != c.DraftVersion) || _draftJobs >= 4 || _draftBaking.Contains(uid) || _baking.Contains(uid))
            return DraftStatus(uid, c, ev, false);
        var round = _round; var revision = c.Revision; var epoch = c.DraftEpoch; var lease = c.DraftLeaseGeneration;
        _draftJobs++; _draftBaking.Add(uid);
        try
        {
            var baked = await Task.Run(() => ArtDocument.Bake(document));
            if (round != _round || !Exists(uid) || !TryComp<ArtCanvasComponent>(uid, out var current) || current != c
                || c.Revision != revision || c.DraftEpoch != epoch || c.DraftLeaseGeneration != lease || _removedArt.Contains((c.WorkId, baked.Hash)))
                return DraftStatus(uid, c, ev, false);
            StoreDraft(uid, c, baked, ev.Writer, ev.Sequence);
            return DraftStatus(uid, c, ev, true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Warning($"Rejected art draft: {e.GetType().Name}");
            return DraftStatus(uid, c, ev, false);
        }
        finally { _draftJobs--; _draftBaking.Remove(uid); }
    }
}

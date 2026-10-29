using System.Linq;
using Content.Shared._DeepLagoon.Mapping;

namespace Content.Client._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{
    // The document is disposable; completed draft updates belong to the client tab.
    private void ReplayEditor()
    {
        _snapshotQueue.Clear();
        foreach (var token in _tabs.Keys.OrderBy(token => token != _activeTab).ToArray())
            InTab(token, () =>
            {
                _tab.UploadId = null; _tab.Upload.Clear(); _tab.UploadText = null;
                _tab.PreviewId = null; _tab.Preview.Clear(); _tab.PreviewText = null;
                PublishCurrentState();
            });
        ActivateTab(_activeTab);
    }

    private void RememberPreview()
    {
        foreach (var item in _tab.Preview)
            if (item.Kind == "clear") _tab.RecoveryEdits.Remove(item.Id);
            else _tab.RecoveryEdits[item.Id] = item;
        _tab.RecoveryExpansion = _tab.PreviewExpansion ?? new();
    }

    private void RebaseRecovery(GridEditorPatchEvent patch)
    {
        if (patch.Applied)
        {
            _tab.RecoveryEdits.Clear();
            foreach (var item in patch.FailedItems) _tab.RecoveryEdits[item.Id] = item;
            _tab.RecoveryExpansion = new();
            _tab.LastAppliedVersion = _tab.PatchVersion + 1;
            return;
        }
        foreach (var item in patch.Superseded)
            if (_tab.RecoveryEdits.TryGetValue(item.Id, out var saved) && (saved.Same(item) || saved.Kind == "remove" && item.Kind == "remove"))
                _tab.RecoveryEdits.Remove(item.Id);
        var previous = _tab.State!;
        var expansion = _tab.RecoveryExpansion;
        static int Retain(int old, long value) => old == 0 ? 0 : (int) Math.Clamp(old < 0 ? value : Math.Max(0, value), int.MinValue, int.MaxValue);
        _tab.RecoveryExpansion = new GridEditorExpansion
        {
            Left = Retain(expansion.Left, (long) patch.X - previous.X + expansion.Left),
            Right = Retain(expansion.Right, (long) previous.X + previous.Width + expansion.Right - patch.X - patch.Width),
            Bottom = Retain(expansion.Bottom, (long) patch.Y - previous.Y + expansion.Bottom),
            Top = Retain(expansion.Top, (long) previous.Y + previous.Height + expansion.Top - patch.Y - patch.Height),
        };
    }
}

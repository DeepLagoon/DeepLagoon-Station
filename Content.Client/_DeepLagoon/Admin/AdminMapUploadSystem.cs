using System.IO;
using Content.Client.Administration.Managers;
using Content.Shared._DeepLagoon.Admin;
using Content.Shared.Administration;
using Content.Shared.Ghost;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Shared.Network;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Admin;

public sealed partial class AdminMapUploadSystem : EntitySystem
{
    [Dependency] private IFileDialogManager _dialogs = default!;
    [Dependency] private IClientAdminManager _admins = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IClientNetManager _net = default!;
    [Dependency] private IGameTiming _timing = default!;

    private Guid? _request;
    private Action<string>? _completed;
    private Action<int, int>? _progress;
    private TimeSpan? _expires;
    private byte[]? _uploadBytes;
    private int _sentOffset;
    private int _receivedOffset;

    public override void Initialize()
    {
        SubscribeNetworkEvent<AdminMapUploadResultEvent>(OnResult);
        SubscribeNetworkEvent<AdminMapUploadProgressEvent>(OnProgress);
        InitializePlacement();
    }

    public bool CanUpload => _net.IsConnected && _admins.HasFlag(AdminFlags.Mapping) &&
        _players.LocalEntity is { } uid && HasComp<GhostComponent>(uid);

    public async void PickAndUpload(Action<string> completed, Action<int, int>? progress = null)
    {
        if (_request != null)
        {
            completed("dl-map-upload-busy");
            return;
        }
        if (!CanUpload)
        {
            completed("dl-map-upload-denied");
            return;
        }

        var request = Guid.NewGuid();
        _request = request;
        _expires = null; // Choosing and reading a local file is not a network transfer.
        _completed = completed;
        _progress = progress;
        try
        {
            await using var file = await _dialogs.OpenFile(
                new FileDialogFilters(new FileDialogFilters.Group("yml", "yaml")), FileAccess.Read);
            if (_request != request)
                return;
            if (file == null)
            {
                Finish("dl-map-upload-cancelled");
                return;
            }
            if (!CanUpload)
            {
                Finish("dl-map-upload-denied");
                return;
            }

            // Bound reads even when the file changes after the dialog opens.
            using var data = new MemoryStream();
            var buffer = new byte[AdminMapUploadLimits.ChunkBytes];
            int count;
            while ((count = await file.ReadAsync(buffer)) > 0)
            {
                if (_request != request)
                    return;
                if (data.Length + count > AdminMapUploadLimits.MaxBytes)
                {
                    Finish("dl-map-upload-size");
                    return;
                }
                data.Write(buffer, 0, count);
            }
            if (_request != request)
                return;
            if (data.Length == 0)
            {
                Finish("dl-map-upload-size");
                return;
            }
            if (!CanUpload)
            {
                Finish("dl-map-upload-denied");
                return;
            }

            var bytes = data.ToArray();
            _uploadBytes = bytes;
            _sentOffset = 0;
            _receivedOffset = -1;
            _expires = _timing.RealTime + TimeSpan.FromSeconds(AdminMapUploadLimits.TimeoutSeconds);
            _progress?.Invoke(0, bytes.Length);
            RaiseNetworkEvent(new AdminMapUploadBeginEvent(request, bytes.Length));
        }
        catch (Exception e)
        {
            Log.Warning($"Could not upload a local map: {e}");
            if (_request == request)
                Finish("dl-map-upload-read-error");
        }
    }

    private void OnResult(AdminMapUploadResultEvent ev)
    {
        if (_request == ev.Request)
            Finish(ev.Message);
    }

    private void OnProgress(AdminMapUploadProgressEvent ev)
    {
        if (_request != ev.Request || _uploadBytes is not { } bytes || ev.Total != bytes.Length ||
            ev.Received != _sentOffset || ev.Received <= _receivedOffset)
            return;

        _receivedOffset = ev.Received;
        _expires = _timing.RealTime + TimeSpan.FromSeconds(AdminMapUploadLimits.TimeoutSeconds);
        _progress?.Invoke(ev.Received, ev.Total);
        if (ev.Received == bytes.Length)
        {
            _uploadBytes = null;
            return;
        }

        // Keep only one chunk in flight. Same-tick, unsequenced entity events may be
        // reordered by the server's tick priority queue, even on a reliable channel.
        var offset = _sentOffset;
        _sentOffset = Math.Min(bytes.Length, offset + AdminMapUploadLimits.ChunkBytes);
        RaiseNetworkEvent(new AdminMapUploadChunkEvent(ev.Request, offset, bytes[offset.._sentOffset]));
    }

    private void Finish(string message)
    {
        ClearPlacement();
        var completed = _completed;
        _request = null;
        _completed = null;
        _progress = null;
        _expires = null;
        _uploadBytes = null;
        _sentOffset = 0;
        _receivedOffset = -1;
        completed?.Invoke(message);
    }

    public override void Update(float frameTime)
    {
        UpdatePlacement();
        if (_request is { } request && (!_net.IsConnected || _expires is { } until && until <= _timing.RealTime))
        {
            if (_net.IsConnected)
                RaiseNetworkEvent(new AdminMapPlacementCancelEvent(request));
            Finish("dl-map-upload-interrupted");
        }
    }

    public override void Shutdown()
    {
        ShutdownPlacement();
        Finish("dl-map-upload-interrupted");
        base.Shutdown();
    }
}

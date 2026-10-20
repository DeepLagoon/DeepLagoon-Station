using System.IO;
using System.Linq;
using Content.Shared.GameTicking;
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
    // The engine recreates entity systems on disconnect. Scope the in-memory resume
    // state to this network manager, so it survives that reset without crossing clients.
    private sealed record ResumeState(Guid Request, byte[] Bytes, string Name, string Hash,
        int Received, TimeSpan Expires, string Endpoint);
    private static readonly List<(WeakReference<IClientNetManager> Net, ResumeState State)> Pending = new();
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
    private string _uploadName = "grid.yml";
    private string _uploadHash = "";
    private TimeSpan _retryAt;
    private TimeSpan _lastSend;
    private double _retrySeconds = 5;
    private bool _resendWindow;
    private bool _wasDisconnected;
    private string? _serverEndpoint;
    private Guid? _libraryPlacement;
    private TimeSpan _libraryPoll;
    private readonly Dictionary<int, TimeSpan> _inFlight = new();
    public IReadOnlyList<AdminMapLibraryEntry> Library { get; private set; } = Array.Empty<AdminMapLibraryEntry>();
    public event Action? LibraryChanged;

    public bool IsBusy => _request != null;
    public bool IsConnected => _net.IsConnected;

    public bool ObservePending(Action<string> completed, Action<int, int> progress)
    {
        if (!IsBusy)
            return false;
        _completed = completed;
        _progress = progress;
        if (_uploadBytes is { } bytes)
            progress(Math.Max(0, _receivedOffset), bytes.Length);
        if (HasPreview)
            completed("dl-map-upload-preview");
        return true;
    }

    public void PollLibrary()
    {
        if (_net.IsConnected && _admins.IsAdmin() && _timing.RealTime >= _libraryPoll)
            RefreshLibrary();
    }

    public void RefreshLibrary()
    {
        if (!_net.IsConnected)
            return;
        RaiseNetworkEvent(new AdminMapLibraryRequestEvent());
        _libraryPoll = _timing.RealTime + TimeSpan.FromSeconds(5);
    }

    public void PlaceFromLibrary(Guid id, Action<string> completed)
    {
        if (_request != null || !CanUpload)
        {
            completed(_request != null ? "dl-map-upload-busy" : "dl-map-upload-denied");
            return;
        }
        _request = Guid.NewGuid();
        _serverEndpoint = _net.ServerChannel?.RemoteEndPoint.ToString();
        _libraryPlacement = id;
        _completed = completed;
        _expires = _timing.RealTime + TimeSpan.FromSeconds(AdminMapUploadLimits.TransferTimeoutSeconds);
        RaiseNetworkEvent(new AdminMapLibraryPlaceEvent(_request.Value, id));
        _retryAt = _timing.RealTime + TimeSpan.FromSeconds(5);
    }

    public override void Initialize()
    {
        SubscribeNetworkEvent<AdminMapUploadResultEvent>(OnResult);
        SubscribeNetworkEvent<AdminMapUploadProgressEvent>(OnProgress);
        SubscribeNetworkEvent<AdminMapLibraryEvent>(ev =>
        {
            if (Library.SequenceEqual(ev.Entries))
                return;
            Library = ev.Entries;
            LibraryChanged?.Invoke();
        });
        SubscribeNetworkEvent<RoundRestartCleanupEvent>(_ =>
        {
            Finish("dl-map-upload-interrupted");
            Library = Array.Empty<AdminMapLibraryEntry>();
            LibraryChanged?.Invoke();
        });
        InitializePlacement();
        // Weak keys keep separate clients isolated without retaining their network managers.
        // ConditionalWeakTable is unavailable in the official content sandbox.
        for (var i = Pending.Count - 1; i >= 0; i--)
        {
            var (reference, pending) = Pending[i];
            if (!reference.TryGetTarget(out var net))
            {
                Pending.RemoveAt(i);
                continue;
            }
            if (ReferenceEquals(net, _net))
            {
                Pending.RemoveAt(i);
                if (pending.Expires <= _timing.RealTime)
                    continue;
                _request = pending.Request;
                _uploadBytes = pending.Bytes;
                _uploadName = pending.Name;
                _uploadHash = pending.Hash;
                _receivedOffset = pending.Received;
                _expires = pending.Expires;
                _serverEndpoint = pending.Endpoint;
                _wasDisconnected = true;
            }
        }
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
        _serverEndpoint = _net.ServerChannel?.RemoteEndPoint.ToString();
        _expires = null; // Choosing and reading a local file is not a network transfer.
        _completed = completed;
        _progress = progress;
        try
        {
            var (stream, name) = await _dialogs.GetFileAndName(
                new FileDialogFilters(new FileDialogFilters.Group("yml", "yaml")), FileAccess.Read);
            await using var file = stream;
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
            _uploadName = name ?? "grid.yml";
            if (_uploadName.Length > 128)
                _uploadName = _uploadName[..128];
            _uploadHash = AdminMapUploadHash.Compute(bytes);
            _uploadBytes = bytes;
            _sentOffset = 0;
            _receivedOffset = -1;
            _expires = _timing.RealTime + TimeSpan.FromSeconds(AdminMapUploadLimits.TransferTimeoutSeconds);
            _progress?.Invoke(0, bytes.Length);
            SendBegin(request, bytes.Length);
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

    private void SendBegin(Guid request, int length)
    {
        RaiseNetworkEvent(new AdminMapUploadBeginEvent(request, length, _uploadName, _uploadHash));
        _lastSend = _timing.RealTime;
        _retryAt = _lastSend + TimeSpan.FromSeconds(_retrySeconds);
        _resendWindow = true;
    }

    private void OnProgress(AdminMapUploadProgressEvent ev)
    {
        if (_request != ev.Request || _uploadBytes is not { } bytes || ev.Total != bytes.Length ||
            ev.Received < 0 || ev.Received > bytes.Length || ev.Received < _receivedOffset ||
            ev.Received != bytes.Length && ev.Received % AdminMapUploadLimits.ChunkBytes != 0)
            return;
        var now = _timing.RealTime;
        var advanced = ev.Received > _receivedOffset;
        if (advanced)
        {
            // Account for measured acknowledgement latency, with conservative bounds.
            if (_inFlight.TryGetValue(Math.Max(0, ev.Received - AdminMapUploadLimits.ChunkBytes), out var sent))
                _retrySeconds = Math.Clamp((now - sent).TotalSeconds * 3, 5, 60);
            _receivedOffset = ev.Received;
            _expires = now + TimeSpan.FromSeconds(AdminMapUploadLimits.TransferTimeoutSeconds);
            _progress?.Invoke(ev.Received, ev.Total);
        }
        if (!advanced && !_resendWindow)
            return;
        if (_resendWindow)
        {
            _inFlight.Clear();
            _sentOffset = ev.Received;
            _resendWindow = false;
        }
        foreach (var offset in new List<int>(_inFlight.Keys))
        {
            if (offset < ev.Received)
                _inFlight.Remove(offset);
        }
        // A bounded sliding window keeps bandwidth useful even on high-latency connections.
        var end = (int) Math.Min(bytes.Length, (long) ev.Received +
            AdminMapUploadLimits.WindowChunks * AdminMapUploadLimits.ChunkBytes);
        while (_sentOffset < end)
        {
            var offset = _sentOffset;
            _sentOffset = Math.Min(bytes.Length, offset + AdminMapUploadLimits.ChunkBytes);
            _inFlight[offset] = now;
            RaiseNetworkEvent(new AdminMapUploadChunkEvent(ev.Request, offset, bytes[offset.._sentOffset]));
        }
        _lastSend = now;
        _retryAt = now + TimeSpan.FromSeconds(_retrySeconds);
        // Keep the local bytes until ready/result: the final acknowledgement itself may be lost.
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
        _libraryPlacement = null;
        _inFlight.Clear();
        _retrySeconds = 5;
        _wasDisconnected = false;
        _serverEndpoint = null;
        completed?.Invoke(message);
    }

    public override void Update(float frameTime)
    {
        UpdatePlacement();
        if (_request is not { } request)
            return;
        if (_expires is { } until && until <= _timing.RealTime)
        {
            if (_net.IsConnected)
                RaiseNetworkEvent(new AdminMapPlacementCancelEvent(request));
            Finish("dl-map-upload-interrupted");
            return;
        }
        if (!_net.IsConnected)
        {
            if (HasPreview)
                Finish("dl-map-upload-interrupted");
            else
                _wasDisconnected = true;
            return;
        }
        if (_serverEndpoint != null && _serverEndpoint != _net.ServerChannel?.RemoteEndPoint.ToString())
        {
            Finish("dl-map-upload-interrupted");
            return;
        }
        if (HasPreview || !CanUpload || _timing.RealTime < _retryAt && !_wasDisconnected)
            return;
        if (_wasDisconnected)
            _receivedOffset = -1; // A restarted server may no longer have the previous allocation.
        _wasDisconnected = false;
        if (_uploadBytes is { } bytes)
        {
            // Probe first: the server returns its committed offset, including after reconnect.
            SendBegin(request, bytes.Length);
            _retrySeconds = Math.Min(60, _retrySeconds * 2);
        }
        else if (_libraryPlacement is { } id)
        {
            RaiseNetworkEvent(new AdminMapLibraryPlaceEvent(request, id));
            _retryAt = _timing.RealTime + TimeSpan.FromSeconds(10);
        }
    }

    public override void Shutdown()
    {
        Pending.RemoveAll(entry => !entry.Net.TryGetTarget(out var net) ||
            ReferenceEquals(net, _net));
        // During Disconnect callbacks the network manager may still report connected.
        if (_request is { } request && _uploadBytes is { } bytes &&
            _serverEndpoint is { } endpoint && _expires is { } expires && expires > _timing.RealTime)
        {
            Pending.Add((new WeakReference<IClientNetManager>(_net),
                new ResumeState(request, bytes, _uploadName, _uploadHash,
                    _receivedOffset, expires, endpoint)));
        }
        ShutdownPlacement();
        Finish("dl-map-upload-interrupted");
        base.Shutdown();
    }
}

using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Content.Shared._DeepLagoon.Music;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Network.Transfer;
using Robust.Shared.Player;
using Robust.Server.Player;

namespace Content.Server._DeepLagoon.Music;

public sealed class MusicBroadcastSystem : EntitySystem
{
    [Dependency] private readonly ITransferManager _transfer = default!;
    [Dependency] private readonly ILogManager _log = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;
    [Dependency] private readonly IEntityManager _entities = default!;
    private INetManager NetManager => _net;

    private ISawmill _sawmill = default!;
    private readonly Dictionary<INetChannel, DateTime> _slowest = new();
    private CancellationTokenSource _lifetime = new();

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = _log.GetSawmill("dl.music.transfer");
        _transfer.RegisterTransferMessage(MusicConstants.AckTransferKey, OnAck, NetMessageAccept.Server);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void OnAck(TransferReceivedEvent ev)
    {
        try
        {
            using var reader = new StreamReader(ev.DataStream);
            var text = reader.ReadToEnd();
            if (!float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                return;

            if (!float.IsFinite(seconds) || seconds < 0f || seconds > MusicConstants.MaxAckSeconds)
                return;

            _slowest[ev.Channel] = DateTime.UtcNow.AddSeconds(seconds);
        }
        catch (Exception e)
        {
            _sawmill.Verbose("Music ack read failed: {Error}", e.GetType().Name);
        }
    }

    public float SlowestSeconds()
    {
        var worst = 0f;
        foreach (var (_, deadline) in _slowest)
        {
            var remaining = (float) (deadline - DateTime.UtcNow).TotalSeconds;
            if (remaining > worst)
                worst = remaining;
        }

        return MusicRules.StartDelay(worst);
    }

    public async Task<int> BroadcastAsync(string channel, string trackId, byte[] audio, float startDelay, float gain,
        uint sourceNetEntity = 0, float maxDistance = 10f)
    {
        var sessions = FilterSessions(sourceNetEntity, maxDistance);

        var prepare = new MusicPlaybackNetMessage
        {
            Command = MusicPlaybackCommand.Prepare,
            Channel = channel,
            TrackId = trackId,
            TotalBytes = audio.Length,
            StartDelay = startDelay,
            Gain = gain,
            Source = sourceNetEntity,
            MaxDistance = maxDistance,
        };

        foreach (var session in sessions)
        {
            if (session.Channel.IsConnected)
                NetManager.ServerSendMessage(prepare, session.Channel);
        }

        var frame = MusicFraming.Wrap(channel, trackId, audio);

        var sent = await SendToAllAsync(sessions, frame, _lifetime.Token);

        var play = new MusicPlaybackNetMessage
        {
            Command = MusicPlaybackCommand.Play,
            Channel = channel,
            TrackId = trackId,
            Position = 0f,
            StartDelay = startDelay,
            Gain = gain,
            Source = sourceNetEntity,
            MaxDistance = maxDistance,
        };

        foreach (var session in sessions)
        {
            if (session.Channel.IsConnected)
                NetManager.ServerSendMessage(play, session.Channel);
        }

        _sawmill.Debug("Music {Track} on channel {Channel} broadcast to {Count} clients", trackId, channel, sent);
        return sent;
    }

    private ICommonSession[] FilterSessions(uint sourceNetEntity, float maxDistance)
    {
        var sessions = IoCManager.Resolve<IPlayerManager>().Sessions.ToArray();
        if (sourceNetEntity == 0 || _xform == null)
            return sessions;

        if (!_entities.TryGetEntity(new NetEntity((int) sourceNetEntity), out var speaker))
            return sessions;

        var speakerPos = _xform.GetMapCoordinates(speaker.Value);
        var kept = new List<ICommonSession>(sessions.Length);

        foreach (var session in sessions)
        {
            if (IsInRange(session, speakerPos, maxDistance))
                kept.Add(session);
        }

        return kept.ToArray();
    }

    public bool InRange(ICommonSession session, uint sourceNetEntity, float maxDistance)
    {
        if (sourceNetEntity == 0 || _xform == null)
            return true;

        if (!_entities.TryGetEntity(new NetEntity((int) sourceNetEntity), out var speaker))
            return true;

        return IsInRange(session, _xform.GetMapCoordinates(speaker.Value), maxDistance);
    }

    private bool IsInRange(ICommonSession session, MapCoordinates speakerPos, float maxDistance)
    {
        if (session.AttachedEntity is not { } attached)
            return false;

        var listenerPos = _xform.GetMapCoordinates(attached);
        if (listenerPos.MapId != speakerPos.MapId)
            return false;

        return (listenerPos.Position - speakerPos.Position).Length() <= maxDistance;
    }

    private async Task<int> SendToAllAsync(ICommonSession[] sessions, byte[] frame, CancellationToken cancel)
    {
        var tasks = new List<Task<bool>>(sessions.Length);

        foreach (var session in sessions)
        {
            if (session.Channel.IsConnected)
                tasks.Add(SendOneAsync(session, frame, cancel));
        }

        if (tasks.Count == 0)
            return 0;

        var results = await Task.WhenAll(tasks);
        return results.Count(ok => ok);
    }

    private async Task<bool> SendOneAsync(ICommonSession session, byte[] frame, CancellationToken cancel)
    {
        try
        {
            await using var stream = _transfer.StartTransfer(session.Channel, MusicConstants.TransferKey);
            await stream.WriteAsync(frame, cancel);
            await stream.FlushAsync(cancel);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception e)
        {
            _sawmill.Verbose("Music send to {Player} skipped: {Error}", session.Name, e.GetType().Name);
            return false;
        }
    }

    public async Task<bool> SendTrackAsync(ICommonSession session, string channel, string trackId, byte[] audio,
        float gain, uint sourceNetEntity, float maxDistance)
    {
        if (!session.Channel.IsConnected)
            return false;

        try
        {
            NetManager.ServerSendMessage(new MusicPlaybackNetMessage
            {
                Command = MusicPlaybackCommand.Prepare,
                Channel = channel,
                TrackId = trackId,
                TotalBytes = audio.Length,
                Gain = gain,
                Source = sourceNetEntity,
                MaxDistance = maxDistance,
            }, session.Channel);

            var frame = MusicFraming.Wrap(channel, trackId, audio);

            await using var stream = _transfer.StartTransfer(session.Channel, MusicConstants.TransferKey);
            await stream.WriteAsync(frame, _lifetime.Token);
            await stream.FlushAsync(_lifetime.Token);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception e)
        {
            _sawmill.Verbose("Music send to {Player} skipped: {Error}", session.Name, e.GetType().Name);
            return false;
        }
    }

    public void SendControl(ICommonSession session, string channel, MusicPlaybackCommand command, float position,
        float gain, uint sourceNetEntity, float maxDistance)
    {
        if (!session.Channel.IsConnected)
            return;

        NetManager.ServerSendMessage(new MusicPlaybackNetMessage
        {
            Command = command,
            Channel = channel,
            Position = position,
            Gain = gain,
            Source = sourceNetEntity,
            MaxDistance = maxDistance,
        }, session.Channel);
    }

    public void SendControl(string channel, MusicPlaybackCommand command, float position = 0f, float gain = 0.75f,
        uint sourceNetEntity = 0, float maxDistance = 10f)
    {
        var message = new MusicPlaybackNetMessage
        {
            Command = command,
            Channel = channel,
            Position = position,
            Gain = gain,
            Source = sourceNetEntity,
            MaxDistance = maxDistance,
        };

        foreach (var session in FilterSessions(sourceNetEntity, maxDistance))
        {
            if (session.Channel.IsConnected)
                NetManager.ServerSendMessage(message, session.Channel);
        }
    }

    public void SendControlAll(string channel, MusicPlaybackCommand command, float position = 0f, float gain = 0.75f)
    {
        var message = new MusicPlaybackNetMessage
        {
            Command = command,
            Channel = channel,
            Position = position,
            Gain = gain,
        };

        foreach (var session in IoCManager.Resolve<IPlayerManager>().Sessions)
        {
            if (session.Channel.IsConnected)
                NetManager.ServerSendMessage(message, session.Channel);
        }
    }
}

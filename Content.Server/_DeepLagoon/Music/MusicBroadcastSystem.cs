using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Content.Shared._DeepLagoon.Music;
using Robust.Shared.Configuration;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Network.Transfer;
using Robust.Shared.Player;
using Robust.Server.Player;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Music;

public sealed class MusicBroadcastSystem : EntitySystem
{
    [Dependency] private readonly ITransferManager _transfer = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly ILogManager _log = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly INetManager _net = default!;
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

    public async Task<int> BroadcastAsync(string trackId, byte[] audio, float startDelay, float gain,
        uint sourceNetEntity = 0, float maxDistance = 10f)
    {
        var sessions = IoCManager.Resolve<IPlayerManager>().Sessions.ToArray();

        var prepare = new MusicPlaybackNetMessage
        {
            Command = MusicPlaybackCommand.Prepare,
            TrackId = trackId,
            TotalBytes = audio.Length,
            StartDelay = startDelay,
            Gain = gain,
            Source = sourceNetEntity,
            MaxDistance = maxDistance,
        };
        NetManager.ServerSendToAll(prepare);

        var idBytes = System.Text.Encoding.UTF8.GetBytes(trackId);
        var framed = new byte[4 + idBytes.Length + audio.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed, idBytes.Length);
        idBytes.CopyTo(framed, 4);
        audio.CopyTo(framed, 4 + idBytes.Length);

        var sent = 0;
        foreach (var session in sessions)
        {
            if (!session.Channel.IsConnected)
                continue;

            try
            {
                await using var stream = _transfer.StartTransfer(session.Channel, MusicConstants.TransferKey);
                await stream.WriteAsync(framed, _lifetime.Token);
                await stream.FlushAsync(_lifetime.Token);
                sent++;
            }
            catch (OperationCanceledException)
            {
                return sent;
            }
            catch (Exception e)
            {
                _sawmill.Verbose("Music send to {Player} skipped: {Error}", session.Name, e.GetType().Name);
            }
        }

        NetManager.ServerSendToAll(new MusicPlaybackNetMessage
        {
            Command = MusicPlaybackCommand.Play,
            TrackId = trackId,
            Position = 0f,
            StartDelay = startDelay,
            Gain = gain,
            Source = sourceNetEntity,
            MaxDistance = maxDistance,
        });

        _sawmill.Debug("Music {Track} broadcast to {Count} clients", trackId, sent);
        return sent;
    }

    public async Task<bool> SendTrackAsync(ICommonSession session, string trackId, byte[] audio, float gain,
        uint sourceNetEntity, float maxDistance)
    {
        if (!session.Channel.IsConnected)
            return false;

        try
        {
            NetManager.ServerSendMessage(new MusicPlaybackNetMessage
            {
                Command = MusicPlaybackCommand.Prepare,
                TrackId = trackId,
                TotalBytes = audio.Length,
                Gain = gain,
                Source = sourceNetEntity,
                MaxDistance = maxDistance,
            }, session.Channel);

            var idBytes = System.Text.Encoding.UTF8.GetBytes(trackId);
            var framed = new byte[4 + idBytes.Length + audio.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed, idBytes.Length);
            idBytes.CopyTo(framed, 4);
            audio.CopyTo(framed, 4 + idBytes.Length);

            await using var stream = _transfer.StartTransfer(session.Channel, MusicConstants.TransferKey);
            await stream.WriteAsync(framed, _lifetime.Token);
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

    public void SendControl(ICommonSession session, MusicPlaybackCommand command, float position, float gain,
        uint sourceNetEntity, float maxDistance)
    {
        if (!session.Channel.IsConnected)
            return;

        NetManager.ServerSendMessage(new MusicPlaybackNetMessage
        {
            Command = command,
            Position = position,
            Gain = gain,
            Source = sourceNetEntity,
            MaxDistance = maxDistance,
        }, session.Channel);
    }

    public void SendControl(MusicPlaybackCommand command, float position = 0f, float gain = 0.75f,
        uint sourceNetEntity = 0, float maxDistance = 10f) =>
        NetManager.ServerSendToAll(new MusicPlaybackNetMessage
        {
            Command = command,
            Position = position,
            Gain = gain,
            Source = sourceNetEntity,
            MaxDistance = maxDistance,
        });
}

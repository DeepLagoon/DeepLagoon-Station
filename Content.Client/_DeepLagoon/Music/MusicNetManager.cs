using System;
using Content.Shared._DeepLagoon.Music;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Network.Transfer;

namespace Content.Client._DeepLagoon.Music;

public sealed partial class MusicNetManager
{
    [Dependency] private IClientNetManager _net = default!;
    [Dependency] private ILogManager _log = default!;
    [Dependency] private IEntitySystemManager _systems = default!;

    private ISawmill _sawmill = default!;

    public void Initialize()
    {
        _sawmill = _log.GetSawmill("dl.music.net");
        _net.RegisterNetMessage<MusicPlaybackNetMessage>(OnPlayback);
        // Transfer registrations live for the application, while ECS is recreated
        // on reconnect. Resolve the current receiver rather than retaining the old world.
        _transfer.RegisterTransferMessage(MusicConstants.TransferKey, ev =>
        {
            if (_systems.TryGetEntitySystem<MusicClientTransferSystem>(out var receiver))
                receiver.OnTrack(ev);
            else
                ev.DataStream.Dispose();
        }, NetMessageAccept.Client);
    }

    private void OnPlayback(MusicPlaybackNetMessage message)
    {
        MusicAudioSystem audio;
        try
        {
            audio = _systems.GetEntitySystem<MusicAudioSystem>();
        }
        catch (Exception)
        {
            _sawmill.Debug("Music playback command ignored: audio system not ready");
            return;
        }

        var channel = message.Channel.Length > 0 ? message.Channel : MusicChannels.Global;

        switch (message.Command)
        {
            case MusicPlaybackCommand.Stop:
                audio.Finish(channel);
                return;

            case MusicPlaybackCommand.Prepare:
                audio.Prepare(channel, message.TrackId, message.Source, message.MaxDistance, message.StartDelay,
                    message.Gain);
                return;

            case MusicPlaybackCommand.Gain:
                audio.SetGain(channel, message.Gain);
                return;

            case MusicPlaybackCommand.Pause:
                audio.Pause(channel);
                return;

            case MusicPlaybackCommand.Play:
                audio.SetGain(channel, message.Gain);
                audio.Play(channel, message.Position);
                return;

            case MusicPlaybackCommand.Seek:
                audio.Seek(channel, message.Position);
                return;

            default:
                _sawmill.Warning("Unknown music playback command {Command}", message.Command);
                return;
        }
    }
}

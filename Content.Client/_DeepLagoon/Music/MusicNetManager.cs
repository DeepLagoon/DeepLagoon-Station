using System;
using Content.Shared._DeepLagoon.Music;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;

namespace Content.Client._DeepLagoon.Music;

public sealed partial class MusicNetManager
{
    [Dependency] private IClientNetManager _net = default!;
    [Dependency] private ILogManager _log = default!;
    [Dependency] private IEntitySystemManager _systems = default!;
    [Dependency] private IEntityManager _entManager = default!;

    private ISawmill _sawmill = default!;
    private string _launchId = string.Empty;

    public void Initialize()
    {
        _sawmill = _log.GetSawmill("dl.music.net");
        _net.RegisterNetMessage<MusicPlaybackNetMessage>(OnPlayback);
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

        switch (message.Command)
        {
            case MusicPlaybackCommand.Stop:
                _launchId = string.Empty;
                audio.Finish();
                return;

            case MusicPlaybackCommand.Prepare:
                _launchId = message.TrackId;
                audio.Prepare(message.TrackId, message.Source, message.MaxDistance, message.StartDelay,
                    message.Gain);
                return;

            case MusicPlaybackCommand.Gain:
                audio.SetGain(message.Gain);
                return;

            case MusicPlaybackCommand.Pause:
                audio.Pause();
                return;
        }

        if (message.TrackId.Length > 0 && message.TrackId != _launchId)
        {
            _sawmill.Debug("Music command {Command} ignored: belongs to {Track}", message.Command, message.TrackId);
            return;
        }

        switch (message.Command)
        {
            case MusicPlaybackCommand.Play:
                audio.SetGain(message.Gain);
                audio.Play(message.Position);
                break;

            case MusicPlaybackCommand.Seek:
                audio.Seek(message.Position);
                break;

            default:
                _sawmill.Warning("Unknown music playback command {Command}", message.Command);
                break;
        }
    }
}

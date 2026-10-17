using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Client.Audio;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Content.Shared.Audio;
using Content.Shared.Audio.Events;
using Content.Shared.CCVar;
using Robust.Client.State;
using Robust.Client.UserInterface;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using Robust.Shared.Maths;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class LobbyMusicTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task PauseKeepsLocalStreamPositionAndDefersAutomaticAdvanceUntilResume()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true, Destructive = true });
        var infoField = typeof(ContentAudioSystem).GetField("_lobbySoundtrackInfo", Private)!;
        object Info(ContentAudioSystem audio) => infoField.GetValue(audio)!;
        EntityUid Stream(ContentAudioSystem audio) => (EntityUid)Info(audio).GetType().GetProperty("MusicStreamEntityUid")!.GetValue(Info(audio))!;
        TimeSpan Deadline(ContentAudioSystem audio) => (TimeSpan)Info(audio).GetType().GetProperty("NextTrackOn")!.GetValue(Info(audio))!;
        void Receive(ContentAudioSystem audio, string[] files) => typeof(ContentAudioSystem).GetMethod("OnLobbySongChanged", Private)!
            .Invoke(audio, new object[] { new LobbyPlaylistChangedEvent(files) });
        string[] serverPlaylist = null;
        EntityUid original = default;
        TimeSpan pausedAt = default, deadline = default;
        const float offset = 12.5f;
        string pauseCaption = null;
        await pair.Server.WaitAssertion(() =>
        {
            var audio = pair.Server.System<Content.Server.Audio.ContentAudioSystem>();
            serverPlaylist = ((string[])audio.GetType().GetField("_lobbyPlaylist", Private)!.GetValue(audio)!).ToArray();
        });
        await pair.Client.WaitAssertion(() =>
        {
            var audio = pair.Client.System<ContentAudioSystem>();
            pair.Client.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.LobbyMusicEnabled, true);
            Receive(audio, serverPlaylist.Take(2).ToArray());
            original = Stream(audio);
            pair.Client.System<Robust.Client.Audio.AudioSystem>().SetPlaybackPosition(original, offset);
            var player = ((LobbyState)pair.Client.ResolveDependency<IStateManager>().CurrentState).Lobby!.MusicPlayer;
            pauseCaption = player.Pause.Text;
            var title = player.TrackTitle;
            deadline = Deadline(audio);
            pausedAt = pair.Client.ResolveDependency<IGameTiming>().CurTime;
            Assert.That(player.Pause.Disabled, Is.False);
            Assert.That(audio.ToggleLobbyMusicPaused(), Is.True);
            Assert.That(audio.IsLobbyMusicPaused, Is.True);
            Assert.That(pair.Client.EntMan.GetComponent<AudioComponent>(original).State, Is.EqualTo(AudioState.Paused));
            Assert.That(player.Pause.Text, Is.Not.EqualTo(pauseCaption));
            Assert.That(player.TrackTitle, Is.EqualTo(title), "Pausing must retain metadata and cover");

            var info = Info(audio);
            infoField.SetValue(audio, Activator.CreateInstance(info.GetType(), audio.CurrentLobbyTrack, TimeSpan.Zero, original));
            typeof(ContentAudioSystem).GetMethod("UpdateLobbyMusic", Private)!.Invoke(audio, null);
            Assert.That(Stream(audio), Is.EqualTo(original), "Even an overdue playlist timer cannot advance while paused");
            infoField.SetValue(audio, info);
            Receive(audio, serverPlaylist.Take(2).ToArray());
            Assert.That(audio.IsLobbyMusicPaused, Is.True, "Repeated server playlists must preserve a personal pause");
        });
        await pair.Client.WaitRunTicks(5);
        await pair.Client.WaitAssertion(() =>
        {
            var audio = pair.Client.System<ContentAudioSystem>();
            var timing = pair.Client.ResolveDependency<IGameTiming>();
            Assert.That(timing.CurTime, Is.GreaterThan(pausedAt));
            var elapsed = timing.CurTime - pausedAt;
            var component = pair.Client.EntMan.GetComponent<AudioComponent>(original);
            Assert.That(component.PlaybackPosition, Is.EqualTo(offset).Within(0.01));
            Assert.That(audio.ToggleLobbyMusicPaused(), Is.True);
            Assert.That(audio.IsLobbyMusicPaused, Is.False);
            Assert.That(Stream(audio), Is.EqualTo(original), "Resume must reuse the same local stream");
            Assert.That(component.State, Is.EqualTo(AudioState.Playing));
            Assert.That(component.PlaybackPosition, Is.EqualTo(offset).Within(0.01), "Resume must not seek back to zero");
            Assert.That(Deadline(audio), Is.EqualTo(deadline + elapsed));
            var player = ((LobbyState)pair.Client.ResolveDependency<IStateManager>().CurrentState).Lobby!.MusicPlayer;
            Assert.That(player.Pause.Text, Is.EqualTo(pauseCaption));

            audio.ToggleLobbyMusicPaused();
            Assert.That(audio.SwitchLobbyTrack(1), Is.True);
            Assert.That(audio.IsLobbyMusicPaused, Is.True, "Choosing another track must preserve the listener's pause");
            Assert.That(Stream(audio), Is.Not.EqualTo(original));
            Assert.That(pair.Client.EntMan.GetComponent<AudioComponent>(Stream(audio)).State, Is.EqualTo(AudioState.Paused));
            audio.ToggleLobbyMusicPaused();

            Receive(audio, new[] { serverPlaylist[0] });
            Assert.That(audio.CanSwitchLobbyTrack, Is.False);
            Assert.That(audio.CanPauseLobbyMusic, Is.True, "Pause must also work for a one-track playlist");
            Assert.That(player.Pause.Disabled, Is.False);
            audio.ToggleLobbyMusicPaused();
            pair.Client.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.LobbyMusicEnabled, false);
            Assert.That(audio.CurrentLobbyTrack, Is.Null);
            Assert.That(audio.IsLobbyMusicPaused, Is.False);
            Assert.That(player.Pause.Disabled, Is.True);
            Assert.That(audio.ToggleLobbyMusicPaused(), Is.False);
        });
        await pair.Server.WaitAssertion(() =>
        {
            var audio = pair.Server.System<Content.Server.Audio.ContentAudioSystem>();
            Assert.That(audio.GetType().GetField("_lobbyPlaylist", Private)!.GetValue(audio), Is.EqualTo(serverPlaylist));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LocalSelectionWrapsStopsOldStreamsAndSurvivesDuplicatePlaylistAndMute()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true, Destructive = true });
        string[] serverPlaylist = null;
        await pair.Server.WaitAssertion(() =>
        {
            var audio = pair.Server.System<Content.Server.Audio.ContentAudioSystem>();
            serverPlaylist = ((string[])audio.GetType().GetField("_lobbyPlaylist", Private)!.GetValue(audio)!).ToArray();
        });
        await pair.Client.WaitAssertion(() =>
        {
            var audio = pair.Client.System<ContentAudioSystem>();
            var config = pair.Client.ResolveDependency<IConfigurationManager>();
            config.SetCVar(CCVars.LobbyMusicEnabled, true);
            var playlist = new[] { serverPlaylist[0], serverPlaylist[1] };
            void Receive(string[] files) => typeof(ContentAudioSystem).GetMethod("OnLobbySongChanged", Private)!
                .Invoke(audio, new object[] { new LobbyPlaylistChangedEvent(files) });
            EntityUid Stream()
            {
                var info = typeof(ContentAudioSystem).GetField("_lobbySoundtrackInfo", Private)!.GetValue(audio)!;
                return (EntityUid)info.GetType().GetProperty("MusicStreamEntityUid")!.GetValue(info)!;
            }

            Receive(playlist);
            Assert.That(audio.CurrentLobbyTrack, Is.EqualTo(playlist[0]));
            var previousStream = Stream();
            Assert.That(audio.SwitchLobbyTrack(-1), Is.True);
            Assert.That(audio.CurrentLobbyTrack, Is.EqualTo(playlist[1]), "Previous wraps from the first to the last track");
            Assert.That(pair.Client.EntMan.IsQueuedForDeletion(previousStream), Is.True, "Switching must stop the old audio stream");
            Assert.That(audio.SwitchLobbyTrack(1), Is.True);
            Assert.That(audio.CurrentLobbyTrack, Is.EqualTo(playlist[0]));
            audio.SwitchLobbyTrack(1);
            var selectedStream = Stream();
            Receive(playlist.ToArray());
            Assert.That(Stream(), Is.EqualTo(selectedStream), "A repeated server playlist cannot restart a personal selection");
            Assert.That(audio.CurrentLobbyTrack, Is.EqualTo(playlist[1]));
            var infoField = typeof(ContentAudioSystem).GetField("_lobbySoundtrackInfo", Private)!;
            var infoType = infoField.GetValue(audio)!.GetType();
            infoField.SetValue(audio, Activator.CreateInstance(infoType, playlist[1], TimeSpan.Zero, selectedStream));
            typeof(ContentAudioSystem).GetMethod("UpdateLobbyMusic", Private)!.Invoke(audio, null);
            Assert.That(audio.CurrentLobbyTrack, Is.EqualTo(playlist[0]), "Finishing a track advances the same local selection");
            Assert.That(pair.Client.EntMan.IsQueuedForDeletion(selectedStream), Is.True);
            audio.SwitchLobbyTrack(1);

            config.SetCVar(CCVars.LobbyMusicEnabled, false);
            Assert.That(audio.CurrentLobbyTrack, Is.Null);
            Assert.That(audio.SwitchLobbyTrack(1), Is.False);
            Receive(playlist.Reverse().ToArray());
            Assert.That(audio.CurrentLobbyTrack, Is.Null, "Receiving a playlist while muted must not start audio");
            config.SetCVar(CCVars.LobbyMusicEnabled, true);
            Assert.That(audio.CurrentLobbyTrack, Is.EqualTo(playlist[1]), "The playlist received while muted must remain available");

            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            Assert.That(prototypes.EnumeratePrototypes<LobbyMusicPrototype>().Any(), Is.True);
            prototypes.LoadString("""
                - type: lobbyMusic
                  id: LobbyMusicCoverTest
                  path: /Audio/_DeepLagoon/UserInterface/lobby_click.ogg
                  name: Covered
                  artist: Artist
                  cover: /Textures/_DeepLagoon/Interface/Lobby/console-frame.png
                """);
            prototypes.ResolveResults();
            var metadata = prototypes.Index<LobbyMusicPrototype>("LobbyMusicCoverTest");
            using var player = new LobbyMusicPlayer();
            player.SetTrack("A long track title [color=red] stays plain text", "Artist", null, true);
            Assert.That(player.HasCover, Is.False);
            Assert.That(player.TrackTitle, Does.Contain("[color=red]"));
            Assert.That(player.Next.Disabled || player.Previous.Disabled, Is.False);
            player.SetTrack(metadata.Name, metadata.Artist, metadata.Cover, false);
            Assert.That(player.HasCover, Is.True);
            Assert.That(player.TrackTitle, Is.EqualTo("Covered"));
            Assert.That(player.Next.Disabled && player.Previous.Disabled, Is.True);
            foreach (var width in new[] { 400, 300, 200, 80 })
            {
                player.Measure(new Vector2(width, 250));
                player.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(width, player.DesiredSize.Y)));
                Assert.That(player.Width, Is.LessThanOrEqualTo(width));
            }
            var states = pair.Client.ResolveDependency<IStateManager>();
            states.RequestStateChange<Content.Client.Gameplay.GameplayState>();
            Assert.That(audio.CurrentLobbyTrack, Is.Null, "Gameplay must stop lobby audio");
            Assert.That(audio.SwitchLobbyTrack(1), Is.False, "Player controls cannot start lobby audio in gameplay");
            states.RequestStateChange<LobbyState>();
            Assert.That(audio.CurrentLobbyTrack, Is.EqualTo(playlist[1]));
            Receive(Array.Empty<string>());
            Assert.That(audio.CurrentLobbyTrack, Is.Null);
            Assert.That(audio.CanSwitchLobbyTrack, Is.False);
        });
        await pair.Server.WaitAssertion(() =>
        {
            var audio = pair.Server.System<Content.Server.Audio.ContentAudioSystem>();
            Assert.That(audio.GetType().GetField("_lobbyPlaylist", Private)!.GetValue(audio), Is.EqualTo(serverPlaylist),
                "Client changes must not modify the server's playlist or what other clients receive");
        });
        await pair.CleanReturnAsync();
    }
}

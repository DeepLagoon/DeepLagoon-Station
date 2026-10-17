using System.Linq;
using System.IO;
using System.Numerics;
using Content.Server.Audio.Jukebox;
using Content.Shared.Audio.Jukebox;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Serialization;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class JukeboxPlaybackTests
{
    [Test]
    public async Task QueuePlaybackPauseCompletionRepeatAndStopUseTheRealAudioSystem()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var entities = pair.Server.ResolveDependency<IEntityManager>();
        EntityUid uid = default, stream = default;
        JukeboxComponent comp = null;
        JukeboxPrototype track = null;
        await pair.Server.WaitAssertion(() =>
        {
            var serializer = pair.Server.ResolveDependency<IRobustSerializer>();
            T RoundTrip<T>(T value)
            {
                using var bytes = new MemoryStream();
                serializer.Serialize(bytes, value);
                bytes.Position = 0;
                return serializer.Deserialize<T>(bytes);
            }
            var state = RoundTrip(new JukeboxState([new("duplicate-a", "track"), new("duplicate-b", "track")], JukeboxRepeat.Queue, 17, true));
            Assert.That(state.Queue.Select(e => e.Id), Is.EqualTo(new[] { "duplicate-a", "duplicate-b" }));
            Assert.That(state.Repeat, Is.EqualTo(JukeboxRepeat.Queue));
            var library = new JukeboxLibrary();
            library.Favorites.Add("track"); library.Playlists.Add("Вечер", new() { "track" });
            Assert.That(RoundTrip(library).Playlists["Вечер"], Is.EqualTo(new[] { "track" }));
            var map = new EntityCoordinates(entities.System<SharedMapSystem>().CreateMap(), Vector2.Zero);
            uid = entities.SpawnEntity(null, map);
            comp = entities.AddComponent<JukeboxComponent>(uid);
            var system = entities.System<JukeboxSystem>();
            track = pair.Server.ResolveDependency<IPrototypeManager>().EnumeratePrototypes<JukeboxPrototype>().First();
            // Raise with the concrete type: BUI dispatch normally preserves that type.
            void Queue(JukeboxQueueMessage message) => entities.EventBus.RaiseLocalEvent(uid, message);
            Queue(new(JukeboxAction.Add, track.ID)); Queue(new(JukeboxAction.Add, track.ID));
            Assert.That(comp.Queue.Count, Is.EqualTo(2));
            var firstId = comp.Queue[0].Id; var secondId = comp.Queue[1].Id;
            var revision = comp.QueueRevision;
            Queue(new(JukeboxAction.Move, secondId, 0, revision));
            Assert.That(comp.Queue[0].Id, Is.EqualTo(secondId));
            Queue(new(JukeboxAction.Remove, firstId, revision: revision));
            Assert.That(comp.Queue.Count, Is.EqualTo(2), "Stale removals cannot modify the current queue");
            entities.EventBus.RaiseLocalEvent(uid, new JukeboxPlayingMessage());
            Assert.That(comp.Queue.Count, Is.EqualTo(1));
            Assert.That(comp.AudioStream, Is.Not.Null);
            stream = comp.AudioStream!.Value;
            var audio = entities.GetComponent<AudioComponent>(stream);
            Assert.That(audio.State, Is.EqualTo(AudioState.Playing));
            entities.EventBus.RaiseLocalEvent(uid, new JukeboxPauseMessage());
            Assert.That(audio.State, Is.EqualTo(AudioState.Paused));
            system.Update(1);
            Assert.That(comp.AudioStream, Is.EqualTo(stream));
            entities.EventBus.RaiseLocalEvent(uid, new JukeboxPlayingMessage());
            Assert.That(audio.State, Is.EqualTo(AudioState.Playing));
            Queue(new(JukeboxAction.Repeat, index: (int) JukeboxRepeat.Track));
            var length = entities.System<Robust.Server.Audio.AudioSystem>().GetAudioLength(new ResolvedPathSpecifier(track.Path.Path.ToString()));
            entities.System<Robust.Server.Audio.AudioSystem>().SetPlaybackPosition(stream, (float) length.TotalSeconds - .01f);
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var system = entities.System<JukeboxSystem>();
            void Queue(JukeboxQueueMessage message) => entities.EventBus.RaiseLocalEvent(uid, message);
            Assert.That(comp.AudioStream, Is.Not.EqualTo(stream));
            Assert.That(comp.Queue.Count, Is.EqualTo(1), "Track repeat does not consume upcoming tracks");
            Queue(new(JukeboxAction.Next));
            Assert.That(comp.Queue, Is.Empty, "Next overrides track repeat");
            entities.EventBus.RaiseLocalEvent(uid, new JukeboxSetGainMessage(float.NaN));
            Assert.That(comp.Gain, Is.EqualTo(.75f));
            entities.EventBus.RaiseLocalEvent(uid, new JukeboxSetGainMessage(2));
            Assert.That(comp.Gain, Is.EqualTo(1));
            Queue(new(JukeboxAction.Add, track.ID));
            entities.EventBus.RaiseLocalEvent(uid, new JukeboxStopMessage());
            Assert.That(comp.AudioStream, Is.Null);
            Assert.That(comp.Queue.Count, Is.EqualTo(1));
            system.Update(1);
            Assert.That(comp.AudioStream, Is.Null, "Intentional stop never advances/restarts");
            entities.DeleteEntity(uid);
        });
        await pair.CleanReturnAsync();
    }
}

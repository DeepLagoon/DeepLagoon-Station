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
    public async Task ClientCatalogBrowsingDoesNotLoadAudioResources()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var resources = pair.Client.ResolveDependency<Robust.Client.ResourceManagement.IResourceCache>();
        var entities = pair.Client.ResolveDependency<IEntityManager>();
        System.Threading.Tasks.Task<Content.Client.Audio.Jukebox.JukeboxCatalog> catalog = null;
        string[] before = [];
        await pair.Client.WaitAssertion(() =>
        {
            before = resources.GetAllResources<Robust.Client.ResourceManagement.AudioResource>().Select(p => p.Key.ToString()).ToArray();
            catalog = entities.System<Content.Client.Audio.Jukebox.JukeboxSystem>().Catalog;
        });
        for (var i = 0; i < 100 && !catalog.IsCompleted; i++)
            await pair.RunTicksSync(1);
        var index = await catalog.WaitAsync(TimeSpan.FromSeconds(10));
        var page = index.Browse(null, "", 0);
        Assert.That(page.Total, Is.GreaterThan(0));
        Assert.That(page.Rows.Length, Is.LessThanOrEqualTo(25));
        Content.Client.Audio.Jukebox.JukeboxMenu menu = null;
        var frame = typeof(Content.Client.Audio.Jukebox.JukeboxMenu).GetMethod("FrameUpdate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var items = 0;
        await pair.Client.WaitAssertion(() =>
        {
            menu = new Content.Client.Audio.Jukebox.JukeboxMenu();
            menu.Populate(System.Threading.Tasks.Task.FromResult(index));
        });
        for (var i = 0; i < 100 && items == 0; i++)
        {
            await pair.Client.WaitAssertion(() =>
            {
                frame.Invoke(menu, [new FrameEventArgs(.1f)]);
                items = menu.FindControl<Robust.Client.UserInterface.Controls.ItemList>("MusicList").Count;
            });
            await Task.Delay(10);
        }
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(items, Is.EqualTo(Math.Min(25, page.Total)), "Classic fallback also renders one bounded page");
            menu.Dispose();
            var after = resources.GetAllResources<Robust.Client.ResourceManagement.AudioResource>().Select(p => p.Key.ToString()).ToArray();
            Assert.That(after.Except(before), Is.Empty, "Browsing metadata must not decode/cache any audio");
        });
        await pair.CleanReturnAsync();
    }

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
            Assert.That(RoundTrip(new JukeboxMetadataRequest(["track"])).Tracks, Is.EqualTo(new[] { "track" }));
            Assert.That(RoundTrip(new JukeboxMetadataMessage(new() { ["track"] = 123f })).Lengths["track"], Is.EqualTo(123f));
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

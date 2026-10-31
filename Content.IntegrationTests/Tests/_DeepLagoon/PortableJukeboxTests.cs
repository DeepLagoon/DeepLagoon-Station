using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Audio;
using Content.Shared._DeepLagoon.DiscordLink;
using Content.Shared.Audio.Jukebox;
using Content.Shared.Item;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Toggleable;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class PortableJukeboxTests
{
    [Test]
    public async Task BothVariantsPlayWithoutApcAndAnimateOnlyDuringPlayback()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var entities = pair.Server.ResolveDependency<IEntityManager>();
        await pair.Server.WaitAssertion(() =>
        {
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var coordinates = new EntityCoordinates(entities.System<SharedMapSystem>().CreateMap(), Vector2.Zero);
            var track = prototypes.EnumeratePrototypes<JukeboxPrototype>().First();
            foreach (var suffix in new[] { "", "Black" })
            {
                var loadout = prototypes.Index<LoadoutPrototype>("DLLoadoutPortableJukebox" + suffix);
                Assert.That(loadout.PersonalDonor, Is.EqualTo(DonorCategory.Boosty));
                Assert.That(loadout.PersonalDonorTier, Is.EqualTo(1), "Impulse is game feature tier 1");
                Assert.That(entities.System<SharedDiscordBoostSystem>().CanUseDonorItem(loadout.PersonalDonor, loadout.PersonalDonorTier, null), Is.False);
                var uid = entities.SpawnEntity("DLPortableJukebox" + suffix, coordinates);
                var jukebox = entities.GetComponent<JukeboxComponent>(uid);
                var item = entities.GetComponent<ItemComponent>(uid);
                var visuals = entities.System<PortableJukeboxSystem>();
                Assert.That(item.HeldPrefix, Is.EqualTo("idle"));
                entities.EventBus.RaiseLocalEvent(uid, new JukeboxQueueMessage(JukeboxAction.PlayTrack, track.ID));
                Assert.That(jukebox.AudioStream, Is.Not.Null, "Portable jukebox needs no station power");
                visuals.Update(0.2f);
                Assert.That(item.HeldPrefix, Is.EqualTo("playing"));
                Assert.That(entities.System<SharedAppearanceSystem>().TryGetData<bool>(uid, ToggleableVisuals.Enabled, out var enabled), Is.True);
                Assert.That(enabled, Is.True);
                entities.EventBus.RaiseLocalEvent(uid, new JukeboxPauseMessage());
                visuals.Update(0.2f);
                Assert.That(item.HeldPrefix, Is.EqualTo("idle"));
                entities.EventBus.RaiseLocalEvent(uid, new JukeboxPlayingMessage());
                visuals.Update(0.2f);
                Assert.That(item.HeldPrefix, Is.EqualTo("playing"));
                entities.EventBus.RaiseLocalEvent(uid, new JukeboxStopMessage());
                visuals.Update(0.2f);
                Assert.That(item.HeldPrefix, Is.EqualTo("idle"));
                Assert.That(jukebox.AudioStream, Is.Null);
                entities.DeleteEntity(uid);
            }
        });
        await pair.CleanReturnAsync();
    }
}

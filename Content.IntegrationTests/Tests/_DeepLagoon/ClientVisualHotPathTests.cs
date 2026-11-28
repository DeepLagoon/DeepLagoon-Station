using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Client._CE.ZLevels.Core;
using Content.Client.Forensics;
using Content.Shared._CE.ZLevels.Core.Components;
using Content.Shared._CE.ZLevels.Core.EntitySystems;
using Content.Shared.Forensics;
using Content.Shared.StatusEffect;
using Moq;
using Robust.Client.GameObjects;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ClientVisualHotPathTests
{
    [Test]
    public async Task ZPassesPreserveAnimationRotationStatusAndLandingAndScentEffectsDoNotReplay()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var em = pair.Client.EntMan;
            var sprites = em.System<SpriteSystem>();
            var transforms = em.System<TransformSystem>();
            var uid = em.SpawnEntity(null, MapCoordinates.Nullspace);
            var sprite = em.AddComponent<SpriteComponent>(uid);
            sprites.SetSnapCardinals((uid, sprite), false);
            var baseline = new Vector2(.25f, -.5f);
            sprites.SetOffset((uid, sprite), baseline);
            sprites.SetDrawDepth((uid, sprite), 123);
            var z = em.AddComponent<CEZPhysicsComponent>(uid);
            z.LocalPosition = .5f;
            var assembly = typeof(CEClientZLevelsSystem).Assembly;
            var pre = (EntitySystem)em.EntitySysManager.GetEntitySystem(assembly.GetType("Content.Client._CE.ZLevels.Core.CEClientZLevelsPreAnimSystem")!);
            var post = (EntitySystem)em.EntitySysManager.GetEntitySystem(assembly.GetType("Content.Client._CE.ZLevels.Core.CEClientZLevelsPostAnimSystem")!);
            var height = z.LocalPosition * CESharedZLevelsSystem.ZLevelOffset;
            void Check(Vector2 expected) => Assert.That(Vector2.Distance(sprite.Offset, expected), Is.LessThan(.001f));
            for (var i = 0; i < 3; i++)
            {
                pre.FrameUpdate(1f / 60);
                sprites.SetScale((uid, sprite), new Vector2(1 + i * .1f));
                post.FrameUpdate(1f / 60);
                Check(baseline + new Vector2(0, height));
            }
            pre.FrameUpdate(1f / 60);
            var animation = new Vector2(.7f, .8f);
            sprites.SetOffset((uid, sprite), animation);
            post.FrameUpdate(1f / 60);
            Check(animation + new Vector2(0, height));
            transforms.SetLocalRotation(uid, Angle.FromDegrees(90));
            transforms.SnapRenderTransform(uid);
            pre.FrameUpdate(1f / 60);
            post.FrameUpdate(1f / 60);
            Check(baseline + new Vector2(height, 0));
            sprite.NoRotation = true;
            pre.FrameUpdate(1f / 60);
            post.FrameUpdate(1f / 60);
            Check(baseline + new Vector2(0, height));

            var status = em.SpawnEntity(null, new EntityCoordinates(uid, Vector2.Zero));
            var statusSprite = em.AddComponent<SpriteComponent>(status);
            em.AddComponent<StatusEffectsComponent>(status);
            pre.FrameUpdate(1f / 60);
            Assert.That(statusSprite.Offset, Is.EqualTo(new Vector2(0, height)));
            z.LocalPosition = 0;
            pre.FrameUpdate(1f / 60);
            post.FrameUpdate(1f / 60);
            Check(baseline);
            Assert.That(sprite.DrawDepth, Is.EqualTo(123));
            Assert.That(statusSprite.Offset, Is.EqualTo(Vector2.Zero));

            var scentSystem = em.System<ScentTrackerSystem>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var timingField = typeof(ScentTrackerSystem).GetField("_timing", flags)!;
            var playerField = typeof(ScentTrackerSystem).GetField("_playerManager", flags)!;
            var oldTiming = timingField.GetValue(scentSystem);
            var oldPlayer = playerField.GetValue(scentSystem);
            var first = false;
            var timing = new Mock<IGameTiming>();
            timing.SetupGet(value => value.IsFirstTimePredicted).Returns(() => first);
            timing.SetupGet(value => value.CurTime).Returns(TimeSpan.FromSeconds(10));
            var player = new Mock<IPlayerManager>();
            player.SetupGet(value => value.LocalEntity).Returns(uid);
            var trail = em.SpawnEntity(null, MapCoordinates.Nullspace);
            var scent = em.AddComponent<ScentTrailComponent>(trail);
            scent.Scent = "test-target";
            try
            {
                timingField.SetValue(scentSystem, timing.Object);
                playerField.SetValue(scentSystem, player.Object);
                scentSystem.Update(1f / 30); // replay without tracker
                first = true;
                scentSystem.Update(1f / 30); // no tracker
                var tracker = em.AddComponent<ScentTrackerComponent>(uid);
                scentSystem.Update(1f / 30); // no selected scent
                Assert.That(scent.TargetTime, Is.EqualTo(TimeSpan.Zero));
                tracker.Scent = scent.Scent;
                first = false;
                scentSystem.Update(1f / 30);
                Assert.That(scent.TargetTime, Is.EqualTo(TimeSpan.Zero));
                first = true;
                scentSystem.Update(1f / 30);
                Assert.That(scent.TargetTime, Is.EqualTo(TimeSpan.FromSeconds(11)));
                var effects = em.EntityQuery<MetaDataComponent>(true).Where(meta => meta.EntityPrototype?.ID == "ScentTrackEffect").ToArray();
                Assert.That(effects, Has.Length.EqualTo(1));
                first = false;
                scentSystem.Update(1f / 30);
                Assert.That(em.EntityQuery<MetaDataComponent>(true).Count(meta => meta.EntityPrototype?.ID == "ScentTrackEffect"), Is.EqualTo(1));
                foreach (var effect in effects) em.DeleteEntity(effect.Owner);
            }
            finally
            {
                timingField.SetValue(scentSystem, oldTiming);
                playerField.SetValue(scentSystem, oldPlayer);
                em.DeleteEntity(trail);
                em.DeleteEntity(uid);
            }
        });
        await pair.CleanReturnAsync();
    }
}

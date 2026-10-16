using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Physics;
using Content.Server.Chat.Managers;
using Moq;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.UnitTesting.Server;

namespace Content.Tests.Server;

[TestFixture, NonParallelizable]
public sealed class InvalidPhysicsAlertTests
{
    [Test]
    public void QuarantinedEntitiesProduceOneAdminOnlyAlertPerPrototypeAndFixture()
    {
        var chat = new Mock<IChatManager>(MockBehavior.Strict);
        var messages = new List<string>();
        chat.Setup(c => c.SendAdminAlert(It.IsAny<string>())).Callback<string>(messages.Add);
        var locale = new Mock<ILocalizationManager>();
        locale.Setup(l => l.GetString("dl-physics-invalid-fixture-alert", It.IsAny<(string, object)[]>()))
            .Returns((string id, (string, object)[] args) => id + ": " +
                string.Join("; ", args.Select(a => $"{a.Item1}={a.Item2}")));

        var sim = RobustServerSimulation.NewSimulation()
            .RegisterDependencies(d =>
            {
                d.RegisterInstance<IChatManager>(chat.Object);
                d.RegisterInstance<ILocalizationManager>(locale.Object, overwrite: true);
            })
            .RegisterEntitySystems(s => s.LoadExtraSystemType<InvalidPhysicsAlertSystem>())
            .InitializeInstance();
        var em = sim.Resolve<IEntityManager>();
        var (_, map) = sim.CreateMap();
        var fixtures = em.System<FixtureSystem>();
        var alerts = em.System<InvalidPhysicsAlertSystem>();

        for (var i = 0; i < 3; i++)
        {
            var uid = em.Spawn(null, new MapCoordinates(Vector2.Zero, map));
            em.AddComponent<PhysicsComponent>(uid);
            Assert.That(fixtures.TryCreateFixture(uid, new PolygonShape(), "broken"), Is.False);
            Assert.That(em.IsQueuedForDeletion(uid), Is.True);
            // The notification must retain the identity after the entity has gone.
            em.DeleteEntity(uid);
        }
        Assert.That(messages, Is.Empty, "Chat should not run inside physics callbacks");
        alerts.Update(0);
        Assert.That(messages, Has.Count.EqualTo(1));
        Assert.That(messages[0], Does.Contain("count=3"));
        Assert.That(messages[0], Does.Contain("fixture=broken"));
        Assert.That(messages[0], Does.Contain("0 vertices"));
        alerts.Update(0);
        Assert.That(messages, Has.Count.EqualTo(1), "The same warning must not be resent every frame");
        chat.Verify(c => c.SendAdminAlert(It.IsAny<string>()), Times.Once);
        chat.VerifyNoOtherCalls();
    }

    [Test]
    public void ValidEntityDoesNotProduceAnAlertOrGetDeleted()
    {
        var chat = new Mock<IChatManager>(MockBehavior.Strict);
        var sim = RobustServerSimulation.NewSimulation()
            .RegisterDependencies(d => d.RegisterInstance<IChatManager>(chat.Object))
            .RegisterEntitySystems(s => s.LoadExtraSystemType<InvalidPhysicsAlertSystem>())
            .InitializeInstance();
        var em = sim.Resolve<IEntityManager>();
        var (_, map) = sim.CreateMap();
        var uid = em.Spawn(null, new MapCoordinates(Vector2.Zero, map));
        em.AddComponent<PhysicsComponent>(uid);
        var polygon = new PolygonShape();
        polygon.SetAsBox(0.2f, 0.075f);
        Assert.That(em.System<FixtureSystem>().TryCreateFixture(uid, polygon, "valid"), Is.True);
        em.System<InvalidPhysicsAlertSystem>().Update(0);
        Assert.That(em.IsQueuedForDeletion(uid), Is.False);
        chat.VerifyNoOtherCalls();
    }
}

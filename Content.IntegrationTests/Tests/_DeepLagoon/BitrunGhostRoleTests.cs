using System.Linq;
using Content.Server._DeepLagoon.Bitrunning;
using Content.Server.Ghost.Roles;
using Content.Server.Mind;
using Content.Shared.Mind;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BitrunGhostRoleTests
{
    [Test]
    public async Task TemporaryRoleReturnsItsPlayerToThePreviousMind()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            var minds = entities.System<MindSystem>();
            var original = entities.SpawnEntity("MobHuman", map.GridCoords);
            var originalMind = minds.GetOrCreateMind(session.UserId);
            minds.TransferTo(originalMind, original);
            var role = entities.SpawnEntity("BitrunSourceObjEffectMobSpawnGhostRoleHumanVirtualDomainPirate", map.GridCoords);
            var guest = entities.SpawnEntity("BitrunRoleBodyObjEffectMobSpawnGhostRoleHumanVirtualDomainPirate", map.GridCoords);

            entities.System<GhostRoleSystem>().GhostRoleInternalCreateMindAndTransfer(session, role, guest);

            Assert.That(session.AttachedEntity, Is.EqualTo(guest));
            var temporary = entities.GetComponent<BitrunTemporaryRoleComponent>(guest);
            Assert.That(temporary.PreviousMind, Is.EqualTo(originalMind.Owner));
            Assert.That(temporary.TemporaryMind, Is.Not.EqualTo(originalMind.Owner));
            entities.DeleteEntity(guest);
            Assert.That(session.AttachedEntity, Is.EqualTo(original));
            Assert.That(minds.GetMind(session.UserId), Is.EqualTo(originalMind.Owner));
            entities.DeleteEntity(role);
            entities.DeleteEntity(original);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task GlitchEventDoesNotCreateRolesWithoutAnActiveDomain()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true, Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(pair.Server.EntMan.System<BitrunSystem>().SpawnGlitchEvent(), Is.False);
        });
        await pair.CleanReturnAsync();
    }
}

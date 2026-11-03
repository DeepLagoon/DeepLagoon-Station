using Content.IntegrationTests.Tests.Interaction;
using Content.Server._DeepLagoon.Administration;
using Content.Server.Administration.Managers;
using Content.Server.Examine;
using Content.Shared.Ghost;
using Robust.Server.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Placement;

namespace Content.IntegrationTests.Tests._DeepLagoon;

public sealed class AdminProvenanceTests : InteractionTest
{
    protected override string PlayerPrototype => "AdminObserver";

    [Test]
    public async Task SpawnAndComponentEditsKeepSeparateAttributionAndResetConsoleContext()
    {
        await Server.WaitAssertion(() =>
        {
            var admins = Server.ResolveDependency<IAdminManager>();
            var console = Server.ResolveDependency<IServerConsoleHost>();
            admins.PromoteHost(ServerSession);
            var coords = SEntMan.GetCoordinates(TargetCoords);
            var natural = SEntMan.SpawnEntity(null, coords);
            Assert.That(SEntMan.HasComponent<AdminProvenanceComponent>(natural), Is.False);

            EntityUid spawned = default;
            console.RegisterCommand("test_admin_provenance_spawn", "", "", (_, _, _) =>
                spawned = SEntMan.SpawnEntity(null, coords));
            try
            {
                console.ExecuteCommand(ServerSession, "sudo sudo test_admin_provenance_spawn");
            }
            finally
            {
                console.UnregisterCommand("test_admin_provenance_spawn");
            }

            var provenance = SEntMan.GetComponent<AdminProvenanceComponent>(spawned);
            Assert.That(provenance.SpawnedBy, Is.EqualTo(ServerSession.UserId));
            Assert.That(provenance.SpawnedByName, Is.EqualTo(ServerSession.Name));
            Assert.That(provenance.SpawnedAt, Is.Not.Null);
            Assert.That(provenance.ModifiedAt, Is.Null);
            var spawnTime = provenance.SpawnedAt;
            Assert.That(console.ExecutingSession, Is.Null);
            var later = SEntMan.SpawnEntity(null, coords);
            Assert.That(SEntMan.HasComponent<AdminProvenanceComponent>(later), Is.False);

            var net = SEntMan.GetNetEntity(spawned);
            console.ExecuteCommand(ServerSession, $"addcomp {net} Tag");
            Assert.That(provenance.ModifiedBy, Is.EqualTo(ServerSession.UserId));
            Assert.That(provenance.ModifiedByName, Is.EqualTo(ServerSession.Name));
            Assert.That(provenance.ModifiedAt, Is.Not.Null);
            provenance.ModifiedAt = DateTime.UnixEpoch;
            console.ExecuteCommand(ServerSession, $"addcomp {net} Tag");
            Assert.That(provenance.ModifiedAt, Is.EqualTo(DateTime.UnixEpoch), "Rejected duplicate adds must not update attribution");
            console.ExecuteCommand(ServerSession, $"rmcomp {net} Tag");
            Assert.That(provenance.ModifiedAt, Is.GreaterThan(DateTime.UnixEpoch));
            Assert.That(provenance.SpawnedAt, Is.EqualTo(spawnTime));

            // Editing a naturally spawned entity must not invent a spawning administrator.
            console.ExecuteCommand(ServerSession, $"addcomp {SEntMan.GetNetEntity(natural)} Tag");
            var modifiedOnly = SEntMan.GetComponent<AdminProvenanceComponent>(natural);
            Assert.That(modifiedOnly.ModifiedAt, Is.Not.Null);
            Assert.That(modifiedOnly.SpawnedAt, Is.Null);
        });
    }

    [Test]
    public async Task OnlyActiveAdminsInAdminGhostCanSeePlacementAttribution()
    {
        await Server.WaitAssertion(() =>
        {
            var admins = Server.ResolveDependency<IAdminManager>();
            admins.PromoteHost(ServerSession);
            var coords = SEntMan.GetCoordinates(TargetCoords);
            var item = SEntMan.SpawnEntity(null, coords);
            SEntMan.EventBus.RaiseEvent(EventSource.Local,
                new PlacementEntityEvent(item, coords, PlacementEventAction.Create, ServerSession.UserId));
            var provenance = SEntMan.GetComponent<AdminProvenanceComponent>(item);
            Assert.That(provenance.SpawnedBy, Is.EqualTo(ServerSession.UserId));
            var examine = SEntMan.System<ExamineSystem>();
            Assert.That(examine.GetExamineText(item, SPlayer).ToString(), Does.Contain("UTC"));
            Assert.That(examine.GetExamineText(item, SPlayer).ToString(), Does.Contain(ServerSession.Name));

            // An admin in a body, or in a regular observer, does not meet the admin-ghost requirement.
            Assert.That(examine.GetExamineText(item, item).ToString(), Does.Not.Contain("UTC"));
            var ghost = SEntMan.GetComponent<GhostComponent>(SPlayer);
#pragma warning disable RA0002 // Exercise the existing VV-writable ghost interaction property.
            ghost.CanGhostInteract = false;
            Assert.That(examine.GetExamineText(item, SPlayer).ToString(), Does.Not.Contain("UTC"));
            ghost.CanGhostInteract = true;
#pragma warning restore RA0002
            admins.DeAdmin(ServerSession);
            Assert.That(examine.GetExamineText(item, SPlayer).ToString(), Does.Not.Contain("UTC"));

            var ordinary = SEntMan.SpawnEntity(null, coords);
            SEntMan.EventBus.RaiseEvent(EventSource.Local,
                new PlacementEntityEvent(ordinary, coords, PlacementEventAction.Create, ServerSession.UserId));
            Assert.That(SEntMan.HasComponent<AdminProvenanceComponent>(ordinary), Is.False);
        });
    }
}

using Content.IntegrationTests.Tests.Interaction;
using Content.Server._DeepLagoon.Administration;
using Content.Server.Administration.Managers;
using Content.Server.Stack;
using Robust.Server.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Placement;

namespace Content.IntegrationTests.Tests._DeepLagoon;

public sealed class AdminCashProvenanceTests : InteractionTest
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task RemovingCashComponentRestoresOriginalPortions(bool consoleRemoval)
    {
        EntityUid cash = default;
        var otherAdmin = new NetUserId(Guid.NewGuid());
        await Server.WaitAssertion(() =>
        {
            Server.ResolveDependency<IAdminManager>().PromoteHost(ServerSession);
            cash = SpawnCash(100);
            SEntMan.EventBus.RaiseEvent(EventSource.Local,
                new PlacementEntityEvent(cash, SEntMan.GetCoordinates(TargetCoords),
                    PlacementEventAction.Create, ServerSession.UserId));
            var component = SEntMan.GetComponent<AdminCashProvenanceComponent>(cash);
            component.Portions[0].Units = 30;
            component.Portions.Add(new AdminCashPortion { Spawner = otherAdmin, Name = "Other admin", Units = 20 });

            if (consoleRemoval)
                Server.ResolveDependency<IServerConsoleHost>()
                    .ExecuteCommand(ServerSession, $"rmcomp {SEntMan.GetNetEntity(cash)} AdminCashProvenance");
            else
                SEntMan.RemoveComponent<AdminCashProvenanceComponent>(cash);

            Assert.That(SEntMan.HasComponent<AdminCashProvenanceComponent>(cash), Is.False);
            // A retained VV session must not mutate the saved attribution after removal.
            component.Portions[0].Units = 1;
            component.Portions.Clear();
            if (!consoleRemoval)
                SEntMan.AddComponent<AdminCashProvenanceComponent>(cash);
        });
        await Pair.RunTicksSync(2);
        await Server.WaitAssertion(() =>
        {
            AssertPortions(cash, (ServerSession.UserId, ServerSession.Name, 30), (otherAdmin, "Other admin", 20));
            SEntMan.RemoveComponent<AdminCashProvenanceComponent>(cash);
        });
        await Pair.RunTicksSync(2);
        await Server.WaitAssertion(() =>
            AssertPortions(cash, (ServerSession.UserId, ServerSession.Name, 30), (otherAdmin, "Other admin", 20)));
    }

    [Test]
    public async Task SameTickSplitMergeAndSpendingPreserveOnlyRemainingTrackedUnits()
    {
        EntityUid donor = default;
        EntityUid recipient = default;
        EntityUid natural = default;
        await Server.WaitAssertion(() =>
        {
            var stacks = SEntMan.System<StackSystem>();
            var otherAdmin = new NetUserId(Guid.NewGuid());
            donor = SpawnCash(100);
            recipient = SpawnCash(60);
            natural = SpawnCash(10);
            SEntMan.AddComponent<AdminCashProvenanceComponent>(donor).Portions.Add(new AdminCashPortion
                { Spawner = ServerSession.UserId, Name = ServerSession.Name, Units = 40 });
            SEntMan.AddComponent<AdminCashProvenanceComponent>(recipient).Portions.Add(new AdminCashPortion
                { Spawner = otherAdmin, Name = "Other admin", Units = 20 });
            SEntMan.RemoveComponent<AdminCashProvenanceComponent>(donor);
            SEntMan.RemoveComponent<AdminCashProvenanceComponent>(recipient);

            // This is also the lookup used by ATM deposits, before any update can restore the component.
            Assert.That(stacks.CashOrigins(donor, 10)![0].Units, Is.EqualTo(10));
            SEntMan.RemoveComponent<AdminCashProvenanceComponent>(donor);
            var split = stacks.Split(donor, 25, SEntMan.GetCoordinates(TargetCoords));
            Assert.That(split, Is.Not.Null);
            AssertPortions(donor, (ServerSession.UserId, ServerSession.Name, 15));
            AssertPortions(split!.Value, (ServerSession.UserId, ServerSession.Name, 25));

            SEntMan.RemoveComponent<AdminCashProvenanceComponent>(split.Value);
            Assert.That(stacks.TryAdd(split.Value, recipient), Is.True);
            AssertPortions(recipient, (otherAdmin, "Other admin", 20), (ServerSession.UserId, ServerSession.Name, 25));

            SEntMan.RemoveComponent<AdminCashProvenanceComponent>(recipient);
            stacks.SetCount(recipient, 40); // Spend all 45 tracked units, retaining 40 natural credits.
            Assert.That(stacks.CashOrigins(recipient, 40), Is.Null);
            Assert.That(SEntMan.HasComponent<AdminCashProvenanceComponent>(recipient), Is.False);
            Assert.That(stacks.CashOrigins(natural, 10), Is.Null);
        });
        await Pair.RunTicksSync(2);
        await Server.WaitAssertion(() =>
        {
            AssertPortions(donor, (ServerSession.UserId, ServerSession.Name, 15));
            Assert.That(SEntMan.HasComponent<AdminCashProvenanceComponent>(recipient), Is.False);
            Assert.That(SEntMan.HasComponent<AdminCashProvenanceComponent>(natural), Is.False);
        });
    }

    [Test]
    public async Task DeletedCashIsNotRestored()
    {
        EntityUid cash = default;
        await Server.WaitAssertion(() =>
        {
            cash = SpawnCash(100);
            SEntMan.AddComponent<AdminCashProvenanceComponent>(cash).Portions.Add(new AdminCashPortion
                { Spawner = ServerSession.UserId, Name = ServerSession.Name, Units = 100 });
            SEntMan.RemoveComponent<AdminCashProvenanceComponent>(cash);
            SEntMan.DeleteEntity(cash);
        });
        await Pair.RunTicksSync(2);
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.System<AdminCashProvenanceSystem>().GetProvenance(cash), Is.Null));
    }

    private EntityUid SpawnCash(int count)
    {
        var cash = SEntMan.SpawnEntity("SpaceCash", SEntMan.GetCoordinates(TargetCoords));
        SEntMan.System<StackSystem>().SetCount(cash, count);
        return cash;
    }

    private void AssertPortions(EntityUid cash, params (NetUserId Spawner, string Name, int Units)[] expected)
    {
        var portions = SEntMan.GetComponent<AdminCashProvenanceComponent>(cash).Portions;
        Assert.That(portions.Count, Is.EqualTo(expected.Length));
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.That(portions[i].Spawner, Is.EqualTo(expected[i].Spawner));
            Assert.That(portions[i].Name, Is.EqualTo(expected[i].Name));
            Assert.That(portions[i].Units, Is.EqualTo(expected[i].Units));
        }
    }
}

using System.Linq;
using Content.Server._NF.Medical;
using Content.Server._NF.Medical.Components;
using Content.Shared._NF.Medical;
using Content.Shared._NF.Medical.Prototypes;
using Content.Shared.Damage;
using Content.Shared.Mind;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Roles;
using Content.Shared.Stacks;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class MedicalBountyTests
{
    [Test]
    public async Task FixedRewardsUseTheSellersJobAndCannotRedeemABodyTwice()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var minds = entities.System<SharedMindSystem>();
            var roles = entities.System<SharedRoleSystem>();
            var containers = entities.System<SharedContainerSystem>();
            var machine = entities.SpawnEntity("MachineMedicalBountyRedemption", map.GridCoords);
            var redemption = entities.GetComponent<MedicalBountyRedemptionComponent>(machine);
            redemption.TaxAccounts.Clear();
            var container = containers.GetContainer(machine, redemption.BodyContainer);
            int Cash() => entities.EntityQuery<StackComponent>().Where(c => c.StackTypeId == "Credit").Sum(c => c.Count);

            foreach (var (job, expected) in new (string, int)[]
                     {
                         (null, 5000),
                         ("Contractor", 5000),
                         ("Brigmedic", 5000),
                         ("MdMedic", 20000),
                         ("DirectorOfCare", 20000),
                     })
            {
                // Sellers have no ID card: qualification comes from their actual job.
                var seller = entities.SpawnEntity(null, map.GridCoords);
                if (job != null)
                {
                    var mind = minds.CreateMind(null);
                    minds.TransferTo(mind, seller);
                    roles.MindAddJobRole(mind, jobPrototype: job);
                }

                var body = entities.SpawnEntity("MobHuman", map.GridCoords);
                entities.AddComponent(body, new MedicalBountyComponent
                {
                    BountyInitialized = true,
                    Bounty = new MedicalBountyPrototype(),
                    MaxBountyValue = int.MaxValue,
                });
                entities.System<DamageableSystem>().TryChangeDamage(body,
                    new DamageSpecifier { DamageDict = { ["Heat"] = 10 } });
                Assert.That(containers.Insert(body, container), Is.True);

                void Redeem() => entities.EventBus.RaiseLocalEvent(machine,
                    new RedeemMedicalBountyMessage { Actor = seller, UiKey = MedicalBountyRedemptionUiKey.Key });

                var before = Cash();
                Redeem();
                Assert.That(Cash() - before, Is.EqualTo(expected), job ?? "No job");
                Assert.That(container.ContainedEntities, Is.Empty);
                Redeem();
                Assert.That(Cash() - before, Is.EqualTo(expected), "A second request must not pay again.");
                entities.DeleteEntity(seller);
            }

            entities.DeleteEntity(machine);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DeadBodiesCannotBeRedeemedBySendingTheMessageDirectly()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var containers = entities.System<SharedContainerSystem>();
            var machine = entities.SpawnEntity("MachineMedicalBountyRedemption", map.GridCoords);
            var body = entities.SpawnEntity("MobHuman", map.GridCoords);
            entities.AddComponent(body, new MedicalBountyComponent
            {
                BountyInitialized = true,
                Bounty = new MedicalBountyPrototype(),
                MaxBountyValue = int.MaxValue,
            });
            var seller = entities.SpawnEntity(null, map.GridCoords);
            var container = containers.GetContainer(machine, "body_container");
            entities.System<MobStateSystem>().ChangeMobState(body, MobState.Dead);
            Assert.That(containers.Insert(body, container), Is.True);
            entities.EventBus.RaiseLocalEvent(machine,
                new RedeemMedicalBountyMessage { Actor = seller, UiKey = MedicalBountyRedemptionUiKey.Key });
            Assert.That(container.ContainedEntities, Does.Contain(body));
            Assert.That(entities.EntityQuery<StackComponent>().Any(c => c.StackTypeId == "Credit"), Is.False);
            entities.DeleteEntity(machine);
            entities.DeleteEntity(seller);
        });
        await pair.CleanReturnAsync();
    }
}

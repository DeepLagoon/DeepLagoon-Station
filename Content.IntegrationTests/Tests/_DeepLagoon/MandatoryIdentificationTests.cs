using System.Threading.Tasks;
using Content.Server.Station.Systems;
using Content.Shared.Access.Components;
using Content.Shared.Inventory;
using Content.Shared.PDA;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class MandatoryIdentificationTests
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: playTimeTracker
  id: PlayTimeMandatoryIdentification
- type: job
  id: MandatoryIdentification
  name: job-name-contractor
  playTimeTracker: PlayTimeMandatoryIdentification
- type: entity
  parent: BasePDA
  id: MandatoryIdentificationEmptyPDA
";

    [Test]
    public async Task SpawnAlwaysProvidesNamedPdaAndCardRegardlessOfLoadout()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var spawning = entities.System<StationSpawningSystem>();
            var inventory = entities.System<InventorySystem>();
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
            var role = new RoleLoadout("JobContractor");
            role.SetDefault(profile, null, prototypes);
            profile.SetLoadout(role);

            // Default, deliberately removed id slot, and a saved empty selection.
            for (var scenario = 0; scenario < 3; scenario++)
            {
                if (scenario == 1)
                    role.UnequippedSlots.UnionWith(new[] { "id", "jumpsuit", "back", "ears" });
                if (scenario == 2)
                {
                    role.UnequippedSlots.Clear();
                    role.SelectedLoadouts.Clear();
                }

                var mob = spawning.SpawnPlayerMob(map.GridCoords, "Contractor", profile, null);
                try
                {
                    AssertIdentification(mob, profile.Name, "Contractor");
                    Assert.That(inventory.TryGetSlotEntity(mob, "id", out var pda), Is.True);
                    var card = entities.GetComponent<PdaComponent>(pda.Value).ContainedId;
                    spawning.SetPdaAndIdCardData(mob, profile.Name, prototypes.Index<JobPrototype>("Contractor"), null);
                    Assert.That(inventory.TryGetSlotEntity(mob, "id", out var samePda), Is.True);
                    Assert.That(samePda, Is.EqualTo(pda), "Do not replace an existing PDA");
                    Assert.That(entities.GetComponent<PdaComponent>(samePda.Value).ContainedId, Is.EqualTo(card));
                }
                finally { entities.DeleteEntity(mob); }
            }

            // A job without a role loadout still receives identification with no profile.
            var bareMob = spawning.SpawnPlayerMob(map.GridCoords, "MandatoryIdentification", null, null);
            try { AssertIdentification(bareMob, entities.GetComponent<MetaDataComponent>(bareMob).EntityName, "MandatoryIdentification"); }
            finally { entities.DeleteEntity(bareMob); }

            // Preserve a selected PDA and repair its missing card instead of replacing it.
            var existingMob = entities.SpawnEntity("MobHuman", map.GridCoords);
            var emptyPda = entities.SpawnEntity("MandatoryIdentificationEmptyPDA", map.GridCoords);
            Assert.That(inventory.TryEquip(existingMob, emptyPda, "id", silent: true, force: true), Is.True);
            spawning.SpawnPlayerMob(map.GridCoords, "MandatoryIdentification", profile, null, existingMob);
            try
            {
                AssertIdentification(existingMob, profile.Name, "MandatoryIdentification");
                Assert.That(inventory.TryGetSlotEntity(existingMob, "id", out var preserved), Is.True);
                Assert.That(preserved, Is.EqualTo(emptyPda));
            }
            finally { entities.DeleteEntity(existingMob); }

            // A directly equipped card is reused inside the mandatory PDA.
            var cardMob = entities.SpawnEntity("MobHuman", map.GridCoords);
            var existingCard = entities.SpawnEntity("ContractorIDCard", map.GridCoords);
            Assert.That(inventory.TryEquip(cardMob, existingCard, "id", silent: true, force: true), Is.True);
            spawning.SpawnPlayerMob(map.GridCoords, "MandatoryIdentification", profile, null, cardMob);
            try
            {
                AssertIdentification(cardMob, profile.Name, "MandatoryIdentification");
                Assert.That(inventory.TryGetSlotEntity(cardMob, "id", out var cardPda), Is.True);
                Assert.That(entities.GetComponent<PdaComponent>(cardPda.Value).ContainedId, Is.EqualTo(existingCard));
            }
            finally { entities.DeleteEntity(cardMob); }

            void AssertIdentification(EntityUid mob, string name, string job)
            {
                Assert.That(inventory.TryGetSlotEntity(mob, "id", out var pdaUid), Is.True);
                var pda = entities.GetComponent<PdaComponent>(pdaUid.Value);
                Assert.That(pda.PdaOwner, Is.EqualTo(mob));
                Assert.That(pda.OwnerName, Is.EqualTo(name));
                Assert.That(pda.ContainedId, Is.Not.Null);
                var card = entities.GetComponent<IdCardComponent>(pda.ContainedId.Value);
                Assert.That(card.FullName, Is.EqualTo(name));
                Assert.That(card.LocalizedJobTitle, Is.EqualTo(prototypes.Index<JobPrototype>(job).LocalizedName));
                if (job == "Contractor")
                    Assert.That(entities.GetComponent<AccessComponent>(pda.ContainedId.Value).Tags, Is.Not.Empty);
            }
        });
        await pair.CleanReturnAsync();
    }
}

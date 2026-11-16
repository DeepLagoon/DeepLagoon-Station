using System.Linq;
using System.Numerics;
using Content.Server.Players.PlayTimeTracking;
using Content.Server.Preferences.Managers;
using Content.Server.Spawners.EntitySystems;
using Content.Server.Station.Systems;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Storage;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class WeaponCaseLoadoutSpawnTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task PurchasedAk502CaseIsDeliveredAtFinalSpawnLocation(bool handsFull)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            const string loadoutId = "ContractorSpecialGunAk502";
            const string caseId = "WeaponCaseAk502Registered";
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var prefs = pair.Server.ResolveDependency<IServerPreferencesManager>();
            var session = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<PlayTimeTrackingManager>().AddTimeToTracker(session, "JobPassenger", TimeSpan.FromHours(6));
            var selected = prefs.GetPreferences(session.UserId);
            var previous = (HumanoidCharacterProfile) selected.SelectedCharacter;
            var role = new RoleLoadout("JobContractor");
            role.SelectedLoadouts["CivGunCaseSpecial"] = new() { new() { Prototype = loadoutId } };
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithBankBalance(previous.BankBalance)
                .WithSpawnPriorityPreference(SpawnPriorityPreference.Cryosleep).WithLoadout(role);
            prefs.SetProfile(session.UserId, selected.SelectedCharacterIndex, profile, persist: false).GetAwaiter().GetResult();
            role = profile.Loadouts[role.Role.Id];
            Assert.That(role.SelectedLoadouts["CivGunCaseSpecial"].Single().Prototype.Id, Is.EqualTo(loadoutId));
            var totalCost = role.SelectedLoadouts.Values.SelectMany(group => group).Sum(item => prototypes.Index(item.Prototype).Price);
            Assert.That(profile.BankBalance, Is.GreaterThanOrEqualTo(totalCost));

            var hands = entities.System<SharedHandsSystem>();
            var spawning = entities.System<StationSpawningSystem>();
            var transform = entities.System<SharedTransformSystem>();
            var cryoSpawner = entities.SpawnEntity("CryogenicSleepUnitSpawner", map.GridCoords);
            var containers = entities.System<SharedContainerSystem>();
            Assert.That(containers.TryGetContainer(cryoSpawner, "storage", out var container), Is.True);
            Assert.That(containers.Insert(entities.SpawnEntity("MobHuman", map.GridCoords), container), Is.True);
            var cryoAttempt = new PlayerSpawningEvent("Contractor", profile, station: null, session: session, deferStartingGear: true);
            entities.System<ContainerSpawnPointSystem>().HandlePlayerSpawning(cryoAttempt);
            Assert.That(cryoAttempt.SpawnResult, Is.Null);
            Assert.That(prefs.GetPreferences(session.UserId).SelectedCharacter, Is.SameAs(profile),
                "An occupied cryosleep spawn point cannot take payment before falling back to a regular spawn.");

            EntityUid mob;
            if (handsFull)
            {
                mob = spawning.SpawnPlayerMob(map.GridCoords, "Contractor", profile, null, session: session, deferStartingGear: true);
                Assert.That(entities.EntityQuery<MetaDataComponent>().Any(meta => meta.EntityPrototype?.ID == caseId), Is.False);
                Assert.That(hands.TryPickupAnyHand(mob, entities.SpawnEntity("Pen", map.GridCoords)), Is.True);
                Assert.That(hands.TryPickupAnyHand(mob, entities.SpawnEntity("Pen", map.GridCoords)), Is.True);
                transform.SetCoordinates(mob, map.GridCoords.Offset(new Vector2(3, 0)));
                spawning.FinishDeferredInhandGear(mob);
            }
            else
            {
                entities.SpawnEntity("SpawnPointLatejoin", map.GridCoords.Offset(new Vector2(3, 0)));
                mob = spawning.SpawnPlayerCharacterOnStation(null, "Contractor", profile, session: session, deferStartingGear: true)!.Value;
            }
            Assert.That(entities.GetComponent<BankAccountComponent>(mob).Balance, Is.EqualTo(profile.BankBalance - totalCost));
            spawning.FinishDeferredInhandGear(mob);

            var weaponCase = entities.EntityQuery<MetaDataComponent>().Single(meta => meta.EntityPrototype?.ID == caseId).Owner;
            Assert.That(hands.EnumerateHeld(mob).Contains(weaponCase), Is.EqualTo(!handsFull));
            Assert.That(transform.GetMapCoordinates(weaponCase), Is.EqualTo(transform.GetMapCoordinates(mob)));
            var caseContents = entities.GetComponent<StorageComponent>(weaponCase).Container.ContainedEntities
                .Select(item => entities.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID);
            Assert.That(caseContents, Does.Contain("WeaponRifleAK502Registered"));
        });
        await pair.CleanReturnAsync();
    }
}

using System.Linq;
using System.Numerics;
using Content.Server.Players.PlayTimeTracking;
using Content.Server.Preferences.Managers;
using Content.Server.Spawners.EntitySystems;
using Content.Server.Station.Systems;
using Content.Shared._NF.Bank.Components;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.CCVar;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Storage;
using Robust.Shared.Containers;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class WeaponCaseLoadoutSpawnTests
{
    [TestCase(false, null)]
    [TestCase(true, null)]
    [TestCase(false, HandLocation.Left)]
    [TestCase(false, HandLocation.Right)]
    public async Task PurchasedAk502CaseIsDeliveredAtFinalSpawnLocation(bool handsFull, HandLocation? side)
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
            Assert.That(prototypes.Index<LoadoutPrototype>(loadoutId).Price, Is.EqualTo(50_000));
            pair.Server.ResolveDependency<PlayTimeTrackingManager>().AddTimeToTracker(session, "JobPassenger", TimeSpan.FromHours(6));
            var selected = prefs.GetPreferences(session.UserId);
            var previous = (HumanoidCharacterProfile) selected.SelectedCharacter;
            var role = new RoleLoadout("JobContractor");
            role.SelectedLoadouts["CivGunCaseSpecial"] = new() { new() { Prototype = loadoutId } };
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithBankBalance(previous.BankBalance)
                .WithSpawnPriorityPreference(SpawnPriorityPreference.Cryosleep);
            string? targetHand = null;
            string? personalHand = null;
            ItemStorageLocation? magazineLocation = null;
            if (side != null)
            {
                pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.PersonalLoadoutsEnabled, true);
                var startingHands = entities.System<PersonalLoadoutSystem>().StartingHands(profile).ToArray();
                targetHand = startingHands.Single(hand => hand.Location == side).Name;
                personalHand = startingHands.Single(hand => hand.Location != side).Name;
                var caseKey = $"loadout/{loadoutId}/hand/0/{caseId}";
                role.HandAssignments[caseKey] = targetHand;
                var referenceCase = entities.SpawnEntity(caseId, map.GridCoords);
                var firstMagazine = entities.GetComponent<StorageComponent>(referenceCase).StoredItems
                    .First(entry => entities.GetComponent<MetaDataComponent>(entry.Key).EntityPrototype?.ID == "Magazine762x39mmFMJ").Value;
                var rotation = ((int) firstMagazine.Rotation.Degrees + 540) % 360;
                magazineLocation = new ItemStorageLocation(Robust.Shared.Maths.Angle.FromDegrees(rotation), firstMagazine.Position);
                role.BackpackLayout[$"{caseKey}/contents/Magazine762x39mmFMJ/0"] = new(true, 0, 0, 0);
                role.BackpackLayout[$"{caseKey}/contents/Magazine762x39mmFMJ/1"] =
                    new(false, firstMagazine.Position.X, firstMagazine.Position.Y, rotation, caseKey);
                entities.DeleteEntity(referenceCase);
                const string guitar = "DLEELoadoutItemAcousticGuitar";
                var personal = new RoleLoadout(PersonalLoadoutSystem.Role);
                var group = prototypes.Index(personal.Role).Groups.First(id => prototypes.Index(id).Loadouts.Contains(guitar));
                personal.SelectedLoadouts[group] = new() { new() { Prototype = guitar } };
                role.HandAssignments[$"personal/{guitar}/hand/0/AcousticGuitarInstrument"] = personalHand;
                profile = profile.WithLoadout(personal);
            }
            profile = profile.WithLoadout(role);
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
            spawning.FinishDeferredStartingGear(mob);

            var weaponCase = entities.EntityQuery<MetaDataComponent>().Single(meta => meta.EntityPrototype?.ID == caseId).Owner;
            Assert.That(hands.EnumerateHeld(mob).Contains(weaponCase), Is.EqualTo(!handsFull));
            if (targetHand != null)
            {
                Assert.That(hands.TryGetHand(mob, targetHand, out var hand), Is.True);
                Assert.That(hand!.HeldEntity, Is.EqualTo(weaponCase));
                Assert.That(hands.TryGetHand(mob, personalHand!, out var other), Is.True);
                Assert.That(other!.HeldEntity, Is.Not.Null);
                Assert.That(entities.GetComponent<MetaDataComponent>(other.HeldEntity!.Value).EntityPrototype?.ID,
                    Is.EqualTo("AcousticGuitarInstrument"));
            }
            Assert.That(transform.GetMapCoordinates(weaponCase), Is.EqualTo(transform.GetMapCoordinates(mob)));
            var caseContents = entities.GetComponent<StorageComponent>(weaponCase).StoredItems.Select(entry =>
                (Prototype: entities.GetComponent<MetaDataComponent>(entry.Key).EntityPrototype?.ID, Location: entry.Value)).ToArray();
            Assert.That(caseContents.Select(entry => entry.Prototype), Does.Contain("WeaponRifleAK502Registered"));
            if (magazineLocation is { } expected)
            {
                var magazine = caseContents.Single(entry => entry.Prototype == "Magazine762x39mmFMJ");
                Assert.That(magazine.Location.Position, Is.EqualTo(expected.Position));
                Assert.That(magazine.Location.Rotation.Degrees, Is.EqualTo(expected.Rotation.Degrees).Within(0.01));
            }
        });
        await pair.CleanReturnAsync();
    }
}

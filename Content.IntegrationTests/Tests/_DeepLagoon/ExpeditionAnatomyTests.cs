using System;
using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Cleanup;
using Content.Server._DeepLagoon.Procurement;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server.Body.Systems;
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Salvage;
using Content.Server.Salvage.Expeditions;
using Content.Shared.Body.Organ;
using Content.Shared.Body.Part;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Salvage;
using Content.Shared.Salvage.Expeditions;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ExpeditionAnatomyTests
{
    [TestCase("MobSyndicateNavalCaptainA")]
    [TestCase("MobBloodCultistPriest")]
    [TestCase("MobHuman")]
    public async Task NativeAnatomyRemainsUnsalableAfterExtractionTransplantationAndSourceDeletion(string prototype)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var expedition = await pair.CreateTestMap();
        var home = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var body = e.System<BodySystem>();
            var pricing = e.System<PricingSystem>();
            var transforms = e.System<SharedTransformSystem>();
            var mission = e.AddComponent<SalvageExpeditionComponent>(expedition.MapUid);
            mission.Station = e.SpawnEntity(null, MapCoordinates.Nullspace);
            mission.MissionParams = new SalvageMissionParams { MissionType = SalvageMissionType.Elimination };
            mission.Rewards = [];
            e.System<SharedMapSystem>().SetPaused(e.GetComponent<MapComponent>(expedition.MapUid).MapId, true);
            var visitor = e.SpawnEntity("MobHuman", home.GridCoords);
            transforms.SetCoordinates(visitor, expedition.GridCoords);
            Assert.That(e.HasComponent<ExpeditionSalvageComponent>(visitor), Is.False, "Arriving players/visitors are not native expedition loot.");
            var mob = e.SpawnEntity(prototype, expedition.GridCoords);
            e.System<MobStateSystem>().ChangeMobState(mob, MobState.Dead);
            Assert.That(e.HasComponent<ExpeditionSalvageComponent>(mob), Is.True);
            var organs = body.GetBodyOrgans(mob).Select(o => o.Id).ToArray();
            var parts = body.GetBodyChildren(mob).Select(p => p.Id).ToArray();
            Assert.That(organs, Has.Length.EqualTo(7));
            foreach (var uid in organs.Concat(parts).Append(mob))
            {
                Assert.That(e.HasComponent<ExpeditionSalvageComponent>(uid), Is.True);
                Assert.That(e.HasComponent<CargoSellBlacklistComponent>(uid), Is.True);
                Assert.That(pricing.GetPrice(uid), Is.Zero);
                Assert.That(pricing.GetPalletPrice(uid, home.Grid, 1.5, out var basePrice), Is.Zero);
                Assert.That(basePrice, Is.Zero);
            }
            var heart = organs.Single(uid => e.GetComponent<MetaDataComponent>(uid).EntityPrototype!.ID.Contains("Heart"));
            Assert.That(body.RemoveOrgan(heart), Is.True);
            transforms.SetCoordinates(heart, home.GridCoords);
            var patient = e.SpawnEntity("MobHuman", home.GridCoords);
            var patientHeart = body.GetBodyOrgans(patient).Single(o => e.GetComponent<MetaDataComponent>(o.Id).EntityPrototype!.ID == "OrganHumanHeart").Id;
            var patientTorso = e.GetComponent<TransformComponent>(patientHeart).ParentUid;
            Assert.That(body.RemoveOrgan(patientHeart), Is.True);
            var containers = e.System<SharedContainerSystem>();
            // Synthetic NPC organs use older slot metadata; insertion exercises the actual container path.
            Assert.That(containers.Insert(heart, containers.GetContainer(patientTorso, "body_organ_slot_heart")), Is.True);
            Assert.That(body.RemoveOrgan(heart), Is.True);
            transforms.SetCoordinates(heart, home.GridCoords);
            Assert.That(e.HasComponent<ExpeditionSalvageComponent>(heart), Is.True);
            Assert.That(pricing.GetPrice(heart), Is.Zero, "A transplant must never launder provenance.");
            body.GibBody(mob, gibOrgans: false);
            e.DeleteEntity(mob);
            Assert.That(pricing.GetPrice(heart), Is.Zero, "The source entity is no longer needed.");
            Assert.That(pricing.GetPrice(patientHeart), Is.GreaterThan(4000), "Ordinary player organs keep their existing value.");

            var console = e.SpawnEntity("ComputerPalletConsoleNFHighMarket", home.GridCoords);
            var pallet = e.SpawnEntity("CargoPalletSell", home.GridCoords);
            Assert.That(transforms.AnchorEntity(console), Is.True);
            Assert.That(transforms.AnchorEntity(pallet), Is.True);
            transforms.SetCoordinates(patientHeart, home.GridCoords);
            Assert.That(e.System<CargoSystem>().TradingPalletItems(console), Does.Contain(patientHeart),
                "The test pallet must actually accept an ordinary donor organ.");
            Assert.That(e.System<CargoSystem>().TradingPalletItems(console), Does.Not.Contain(heart));
            var crate = e.SpawnEntity("CrateGenericSteel", home.GridCoords);
            var storage = containers.EnsureContainer<Container>(crate, "anatomy-test");
            Assert.That(containers.Insert(heart, storage), Is.True);
            Assert.That(e.System<CargoSystem>().TradingPalletItems(console), Does.Not.Contain(crate));
            Assert.That(pricing.GetPrice(crate), Is.EqualTo(pricing.GetPrice(crate, includeContents: false)),
                "Packing the organ must not add its former static value to the container's price.");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AnatomyUsesNoFoodQuotaAndParkingRetainsItsPermanentBan()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var procurement = e.System<ProcurementSystem>();
            foreach (var proto in new[] { "OrganHumanHeart", "OrganHumanBrain", "OrganSyntheticLiver", "TorsoHuman" })
            {
                var anatomy = e.SpawnEntity(proto, map.GridCoords);
                Assert.That(procurement.Classify(anatomy), Is.EqualTo(ProcurementCategory.Industry));
            }
            var meal = e.SpawnEntity("FoodPizzaMargherita", map.GridCoords);
            Assert.That(procurement.Classify(meal), Is.EqualTo(ProcurementCategory.Food));
            var organ = e.SpawnEntity("OrganHumanHeart", map.GridCoords);
            var chemistry = e.System<Content.Shared.Chemistry.EntitySystems.SharedSolutionContainerSystem>();
            Assert.That(chemistry.TryGetSolution(organ, "food", out var foodSolution, out _), Is.True);
            chemistry.RemoveReagent(foodSolution!.Value, "UncookedAnimalProteins", 5);
            Assert.That(chemistry.TryAddReagent(foodSolution.Value, "Nutriment", 5), Is.True);
            var isMeal = typeof(CargoSystem).GetMethod("HasMealNutrients", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.That(isMeal.Invoke(e.System<CargoSystem>(), [organ, 1]), Is.EqualTo(false),
                "Even an unprotected organ filled with usable nutrients cannot fulfill a funded food order.");
            e.System<ExpeditionSalvageSystem>().ProtectDefender(organ);
            var parking = e.System<ParkingSystem>();
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            var snapshot = parking.FilteredSnapshot(map.Grid);
            e.DeleteEntity(map.Grid);
            var restored = parking.LoadSnapshot(snapshot);
            var children = e.GetComponent<TransformComponent>(restored).ChildEnumerator;
            var found = false;
            while (children.MoveNext(out var child))
                if (e.HasComponent<OrganComponent>(child) && e.HasComponent<ExpeditionSalvageComponent>(child))
                {
                    found = true;
                    Assert.That(e.HasComponent<CargoSellBlacklistComponent>(child), Is.True);
                    Assert.That(e.System<PricingSystem>().GetPrice(child), Is.Zero);
                }
            Assert.That(found, Is.True);
            e.DeleteEntity(restored);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LooseOrgansNeedTenContinuousMinutesAndNoBodyOrRemoteCameraViewer()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        EntityUid organ = default, viewer = default, camera = default;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            organ = e.SpawnEntity("OrganHumanHeart", map.GridCoords);
            viewer = e.SpawnEntity("MobHumanDummy", map.GridCoords.Offset(new Vector2(2000, 0)));
            camera = e.SpawnEntity(null, map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), viewer);
            var cleanup = e.System<AgeCleanupSystem>();
            Assert.That(cleanup.IsDue(organ), Is.False);
            var age = e.GetComponent<AgeCleanupComponent>(organ);
            Assert.That(age.Kind, Is.EqualTo(AgeCleanupKind.LooseOrgan));
            Assert.That(age.Due - age.Since, Is.EqualTo(TimeSpan.FromMinutes(10)));
            var now = pair.Server.ResolveDependency<IGameTiming>().CurTime;
            age.Since = now - TimeSpan.FromSeconds(599);
            age.Due = now + TimeSpan.FromSeconds(1);
            Assert.That(cleanup.IsDue(organ), Is.False);
            age.Due = now;
            Assert.That(cleanup.IsDue(organ), Is.True);
            e.System<SharedTransformSystem>().SetCoordinates(viewer, map.GridCoords);
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            Assert.That(e.System<AgeCleanupSystem>().DeleteEligible([e.GetNetEntity(organ)]), Is.Zero, "An old organ must not vanish while watched.");
            e.System<SharedTransformSystem>().SetCoordinates(viewer, map.GridCoords.Offset(new Vector2(2000, 0)));
            e.System<ViewSubscriberSystem>().AddViewSubscriber(camera, pair.Server.PlayerMan.Sessions.Single());
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            Assert.That(e.System<AgeCleanupSystem>().IsDue(organ), Is.False, "Remote observers also protect the item.");
            // Keep the automatic queue from racing the following container assertions.
            e.GetComponent<AgeCleanupComponent>(organ).Due = pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromSeconds(1);
            e.System<ViewSubscriberSystem>().RemoveViewSubscriber(camera, pair.Server.PlayerMan.Sessions.Single());
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var cleanup = e.System<AgeCleanupSystem>();
            e.GetComponent<AgeCleanupComponent>(organ).Due = pair.Server.ResolveDependency<IGameTiming>().CurTime;
            Assert.That(cleanup.IsDue(organ), Is.True);
            var bag = e.SpawnEntity("ClothingBackpack", map.GridCoords);
            var containers = e.System<SharedContainerSystem>();
            var contents = containers.EnsureContainer<Container>(bag, "anatomy-test");
            Assert.That(containers.Insert(organ, contents), Is.True);
            Assert.That(cleanup.IsDue(organ), Is.False);
            Assert.That(e.HasComponent<AgeCleanupComponent>(organ), Is.False);
            Assert.That(containers.Remove(organ, contents), Is.True);
            e.System<SharedTransformSystem>().SetCoordinates(organ, map.GridCoords);
            Assert.That(cleanup.IsDue(organ), Is.False);
            var age = e.GetComponent<AgeCleanupComponent>(organ);
            Assert.That(age.Due - pair.Server.ResolveDependency<IGameTiming>().CurTime, Is.EqualTo(TimeSpan.FromMinutes(10)));
            age.Due = pair.Server.ResolveDependency<IGameTiming>().CurTime;
            Assert.That(cleanup.DeleteEligible([e.GetNetEntity(organ)]), Is.EqualTo(1));
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.EntityExists(organ), Is.False));
        await pair.CleanReturnAsync();
    }
}

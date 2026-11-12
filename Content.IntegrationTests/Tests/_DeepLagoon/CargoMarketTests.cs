using System.Linq;
using System.Numerics;
using Content.Server._NF.Bank;
using Content.Server._NF.CrateMachine;
using Content.Server._NF.Market.Components;
using Content.Server._NF.Market.Extensions;
using Content.Server._NF.Market.Systems;
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Labels;
using Content.Server.Lathe;
using Content.Server.Power.Components;
using Content.Server.Preferences.Managers;
using Content.Server.Stack;
using Content.Server.Station.Systems;
using Content.Server.Storage.Components;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Market.Events;
using Content.Shared.Cargo.Events;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Maps;
using Content.Shared.Materials;
using Content.Shared.Preferences;
using Content.Shared.Research.Components;
using Content.Shared.Research.Prototypes;
using Content.Shared.Research.Systems;
using Content.Shared.Stacks;
using Content.Shared.Station.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class CargoMarketTests
{
    [Test]
    public async Task CompletedMmcShipmentPaysOnceAndDoesNotRestockMarket()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var cargo = entities.System<CargoSystem>();
            var stacks = entities.System<StackSystem>();
            var station = entities.SpawnEntity(null, map.GridCoords);
            var market = entities.AddComponent<CargoMarketDataComponent>(station);
            var db = entities.AddComponent<StationCargoBountyDatabaseComponent>(station);
            entities.EnsureComponent<StationMemberComponent>(map.Grid).Station = station;
            Assert.That(cargo.TryAddBounty(station, "MMCBountyPlastitanium", db), Is.True);
            var order = db.Bounties.Single();

            var pallet = entities.SpawnEntity("CargoPalletSell", map.GridCoords);
            var coordinates = entities.GetComponent<TransformComponent>(pallet).Coordinates;
            var console = entities.SpawnEntity("ComputerPalletConsoleNFNormalMarket", coordinates);
            var crate = entities.SpawnEntity("CrateGenericSteel", coordinates);
            var label = entities.SpawnEntity("PaperCargoBountyManifest", coordinates);
            cargo.SetupBountyLabel(label, station, order);
            Assert.That(entities.System<ItemSlotsSystem>().TryInsert(crate, LabelSystem.ContainerName, label, null), Is.True);

            var storage = entities.GetComponent<EntityStorageComponent>(crate).Contents;
            var containers = entities.System<SharedContainerSystem>();
            foreach (var count in new[] { 25, 25 })
            {
                var shipment = entities.SpawnEntity("SheetPlastitanium1", coordinates);
                stacks.SetCount(shipment, count);
                Assert.That(containers.Insert(shipment, storage), Is.True);
            }
            Assert.That(cargo.IsBountyComplete(crate, out _), Is.True);

            var regularSale = entities.SpawnEntity("SheetPlastitanium1", coordinates);
            stacks.SetCount(regularSale, 4);
            entities.EventBus.RaiseLocalEvent(console, new CargoPalletSellMessage());

            Assert.That(entities.EntityExists(crate), Is.False);
            Assert.That(entities.EntityExists(regularSale), Is.False);
            Assert.That(db.Bounties, Is.Empty);
            Assert.That(db.History, Has.Count.EqualTo(1));
            Assert.That(market.MarketDataList.Single().Quantity, Is.EqualTo(4));
            Assert.That(market.MarketDataList.Single().Prototype.Id, Is.EqualTo("SheetPlastitanium1"));
            Assert.That(entities.EntityQuery<StackComponent>()
                .Where(stack => stack.StackTypeId == "MMCIndustrialCredit").Sum(stack => stack.Count), Is.EqualTo(500));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OnlyPrintedGearIsCappedAndItsLimitSurvivesMarketPurchase()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            const string armorId = "ClothingOuterHardsuitWarlord";
            var entities = pair.Server.EntMan;
            var coordinates = map.GridCoords.Offset(new Vector2(0.5f, 0.5f));
            var station = entities.SpawnEntity(null, coordinates);
            entities.EnsureComponent<StationMemberComponent>(map.Grid).Station = station;
            var market = entities.AddComponent<CargoMarketDataComponent>(station);
            var pallet = entities.SpawnEntity("CargoPalletSell", coordinates);
            var saleCoordinates = entities.GetComponent<TransformComponent>(pallet).Coordinates;
            var sellConsole = entities.SpawnEntity("ComputerPalletConsoleNFVeryHighMarket", saleCoordinates);
            var pricing = entities.System<PricingSystem>();
            var printingCoordinates = new EntityCoordinates(map.MapUid, 10, 10);
            var lathe = entities.SpawnEntity("AmmoTechFab", printingCoordinates);
            var actor = entities.SpawnEntity("MobHumanDummy", printingCoordinates);
            var session = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.PlayerMan.SetAttachedEntity(session, actor);
            var bank = entities.System<BankSystem>();
            var prefs = pair.Server.ResolveDependency<IServerPreferencesManager>().GetPreferences(session.UserId);
            Assert.That(bank.TryBankDeposit(session, prefs, (HumanoidCharacterProfile) prefs.SelectedCharacter,
                30000, out var balance), Is.True);
            bank.SetConfirmedBankBalance(actor, balance.Value);
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var blueprint = entities.SpawnEntity("BlueprintWarlordSuit", printingCoordinates);
            Assert.That(entities.System<BlueprintSystem>().TryInsertBlueprint(
                (lathe, entities.GetComponent<BlueprintReceiverComponent>(lathe)),
                (blueprint, entities.GetComponent<BlueprintComponent>(blueprint)), actor), Is.True);

            Print(entities, lathe, actor, prototypes.Index(new ProtoId<LatheRecipePrototype>("MonoHardsuitWarlordRecipe")));
            var crafted = entities.EntityQuery<MetaDataComponent>()
                .Single(meta => meta.EntityPrototype?.ID == armorId).Owner;
            Assert.That(pricing.GetPrice(crafted), Is.EqualTo(500));
            var transform = entities.System<SharedTransformSystem>();
            transform.SetCoordinates(crafted, saleCoordinates);
            int CashOnGrid() => entities.EntityQuery<StackComponent, TransformComponent>()
                .Where(entry => entry.Item1.StackTypeId == "Credit" && entry.Item2.GridUid == map.Grid.Owner)
                .Sum(entry => entry.Item1.Count);
            var initialCash = CashOnGrid();
            entities.EventBus.RaiseLocalEvent(sellConsole, new CargoPalletSellMessage());
            Assert.That(entities.EntityExists(crafted), Is.False);
            Assert.That(CashOnGrid() - initialCash, Is.EqualTo(500));

            transform.SetCoordinates(actor, coordinates);
            var marketConsole = entities.SpawnEntity("ComputerMarketConsoleNFNormal", coordinates);
            var cart = entities.GetComponent<MarketConsoleComponent>(marketConsole);

            var found = entities.SpawnEntity(armorId, coordinates);
            var regularPrice = pricing.GetPrice(found);
            Assert.That(regularPrice, Is.GreaterThan(500));
            var sale = new EntitySoldEvent([found], map.Grid);
            entities.EventBus.RaiseEvent(EventSource.Local, ref sale);
            entities.DeleteEntity(found);
            Assert.That(market.MarketDataList.Where(lot => lot.Prototype.Id == armorId).Select(lot => lot.Price),
                Is.EquivalentTo(new[] { 500, regularPrice }));

            entities.EventBus.RaiseLocalEvent(marketConsole, new MarketConsoleCartMessage(2, armorId) { Actor = actor });
            Assert.That(cart.CartDataList.Single().CraftedUnitLimit, Is.EqualTo(500));
            Assert.That(cart.CartDataList.Single().Quantity, Is.EqualTo(1));
            entities.EventBus.RaiseLocalEvent(marketConsole, new MarketConsoleCartMessage(0, armorId, true) { Actor = actor });
            Assert.That(cart.CartDataList, Is.Empty);
            Assert.That(market.MarketDataList.Count(lot => lot.Prototype.Id == armorId), Is.EqualTo(2));

            var machine = entities.SpawnEntity("CrateMachine", coordinates);
            entities.EventBus.RaiseLocalEvent(marketConsole, new MarketConsoleCartMessage(1, armorId) { Actor = actor });
            entities.EventBus.RaiseLocalEvent(marketConsole, new MarketConsoleCartMessage(1, armorId) { Actor = actor });
            Assert.That(cart.CartDataList, Has.Count.EqualTo(2));
            entities.EventBus.RaiseLocalEvent(marketConsole, new MarketPurchaseMessage { Actor = actor });
            entities.EventBus.RaiseLocalEvent(machine, new CrateMachineOpenedEvent(machine));
            var crate = entities.EntityQuery<EntityStorageComponent, TransformComponent>(true)
                .Single(entry => entry.Item2.GridUid == map.Grid.Owner).Item1;
            var purchased = crate.Contents.ContainedEntities;
            Assert.That(purchased.Select(uid => pricing.GetPrice(uid)), Is.EquivalentTo(new[] { 500, regularPrice }));
            var repurchasedCraft = purchased.Single(uid => entities.HasComponent<CraftedPriceComponent>(uid));
            Assert.That(entities.GetComponent<CraftedPriceComponent>(repurchasedCraft).UnitLimit, Is.EqualTo(500));
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AmmunitionFromCraftedLoaderKeepsItsOriginWhenUnloaded()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            const string loaderId = "Magazine30mmHE";
            var entities = pair.Server.EntMan;
            var pricing = entities.System<PricingSystem>();
            var lathe = entities.SpawnEntity("AmmoTechFab", map.GridCoords);
            var actor = entities.SpawnEntity("MobHumanDummy", map.GridCoords);
            var recipe = pair.Server.ResolveDependency<IPrototypeManager>()
                .Index(new ProtoId<LatheRecipePrototype>("Magazine30mmHEAmmoRecipe"));
            Print(entities, lathe, actor, recipe);

            var crafted = entities.EntityQuery<MetaDataComponent>().Single(meta => meta.EntityPrototype?.ID == loaderId).Owner;
            var found = entities.SpawnEntity(loaderId, map.GridCoords);
            Assert.That(pricing.GetPrice(crafted), Is.Zero);
            Assert.That(pricing.GetPrice(found), Is.EqualTo(2000));
            var fromCrafted = new TakeAmmoEvent(1, [], map.GridCoords, actor);
            var fromFound = new TakeAmmoEvent(1, [], map.GridCoords, actor);
            entities.EventBus.RaiseLocalEvent(crafted, fromCrafted);
            entities.EventBus.RaiseLocalEvent(found, fromFound);

            Assert.That(pricing.GetPrice(fromCrafted.Ammo.Single().Entity!.Value), Is.Zero);
            Assert.That(pricing.GetPrice(fromFound.Ammo.Single().Entity!.Value), Is.EqualTo(1));
        });
        await pair.CleanReturnAsync();
    }

    private static void Print(IEntityManager entities, EntityUid machine, EntityUid actor, LatheRecipePrototype recipe)
    {
        var materials = entities.System<SharedMaterialStorageSystem>();
        foreach (var (material, amount) in recipe.Materials)
            Assert.That(materials.TrySetMaterialAmount(machine, material, amount * 2), Is.True);

        entities.GetComponent<ApcPowerReceiverComponent>(machine).Powered = true;
        var lathe = entities.System<LatheSystem>();
        Assert.That(lathe.TryAddToQueue(machine, recipe, 1, actor: actor), Is.True);
        Assert.That(lathe.TryStartProducing(machine), Is.True);
        lathe.FinishProducing(machine);
    }

    [TestCase("CrateTradeSecureNormal", 750, 5750)]
    [TestCase("CrateTradeSecureHigh", 1500, 9000)]
    public async Task CaelestinusCentralPaysDestinationRateForIncomingTradeCrates(
        string cratePrototype, int elsewherePrice, int destinationPrice)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var stationConfig = prototypes.Index(new ProtoId<GameMapPrototype>("Frontier")).Stations["Frontier"];
            entities.System<StationSystem>().InitializeNewStation(stationConfig, [map.Grid.Owner], "Caelestinus Central");

            var pallet = entities.SpawnEntity("CargoPalletSell", map.GridCoords);
            var palletTransform = entities.GetComponent<TransformComponent>(pallet);
            var console = entities.SpawnEntity("ComputerPalletConsoleNFHighMarket", map.GridCoords);
            var pricing = entities.System<PricingSystem>();

            var localCrate = entities.SpawnEntity(cratePrototype, palletTransform.Coordinates);
            Assert.That(pricing.GetPriceWithVendingDiscount(localCrate, map.Grid), Is.EqualTo(elsewherePrice),
                "A crate ordered locally must not receive the destination rate when there is no other destination.");
            entities.DeleteEntity(localCrate);

            var crate = entities.SpawnEntity(cratePrototype, new EntityCoordinates(map.MapUid, 10, 10));
            entities.System<SharedTransformSystem>().SetCoordinates(crate, palletTransform.Coordinates);
            entities.EventBus.RaiseLocalEvent(console, new CargoPalletSellMessage());

            Assert.That(entities.EntityExists(crate), Is.False);
            var payout = entities.EntityQuery<StackComponent, TransformComponent>()
                .Where(entry => entry.Item1.StackTypeId == "Credit" && entry.Item2.GridUid == map.Grid.Owner)
                .Sum(entry => entry.Item1.Count);
            Assert.That(payout, Is.EqualTo(destinationPrice));
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("MaterialBananium", 30)]
    [TestCase("BananiumOre", 20)]
    public async Task SplittingBananiumDoesNotIncreaseItsSalePrice(string prototypeId, int unitPrice)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            const int quantity = 5;
            var entities = pair.Server.EntMan;
            var stacks = entities.System<StackSystem>();
            var pricing = entities.System<PricingSystem>();
            var original = entities.SpawnEntity(prototypeId, map.GridCoords);
            stacks.SetCount(original, quantity);

            var marketValue = pricing.GetPrice(original);
            var saleValue = pricing.GetPriceWithVendingDiscount(original, map.Grid);
            Assert.That(marketValue, Is.EqualTo(quantity * unitPrice));
            Assert.That(saleValue, Is.EqualTo(marketValue));

            var units = Enumerable.Range(1, quantity - 1)
                .Select(_ => stacks.Split(original, 1, map.GridCoords).Value)
                .Append(original)
                .ToArray();
            Assert.That(units.Sum(uid => pricing.GetPrice(uid)), Is.EqualTo(marketValue));
            Assert.That(units.Sum(uid => pricing.GetPriceWithVendingDiscount(uid, map.Grid)), Is.EqualTo(saleValue));

            var prototype = entities.GetComponent<MetaDataComponent>(units[0]).EntityPrototype;
            Assert.That(pricing.GetEstimatedPrice(prototype), Is.EqualTo(unitPrice));
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(5, false)]
    [TestCase(7, true)]
    public async Task BuyingSoldBluespaceReturnsTheSameNumberOfCrystals(int quantity, bool legacyListing)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var coordinates = map.GridCoords.Offset(new Vector2(0.5f, 0.5f));
            var station = entities.SpawnEntity(null, coordinates);
            var market = entities.AddComponent<CargoMarketDataComponent>(station);
            entities.EnsureComponent<StationMemberComponent>(map.Grid).Station = station;

            var materialStorage = entities.SpawnEntity(null, coordinates);
            entities.AddComponent<MaterialStorageComponent>(materialStorage);
            Assert.That(entities.System<SharedMaterialStorageSystem>()
                .TrySetMaterialAmount(materialStorage, "Bluespace", quantity * 100), Is.True);
            var sale = new EntitySoldEvent([materialStorage], map.Grid);
            entities.EventBus.RaiseEvent(EventSource.Local, ref sale);

            var listing = market.MarketDataList.Single();
            Assert.That(listing.Quantity, Is.EqualTo(quantity));
            Assert.That(listing.StackPrototype?.Id, Is.EqualTo("Bluespace"));
            if (legacyListing)
                listing.StackPrototype = listing.Prototype.Id;

            var console = entities.SpawnEntity("ComputerMarketConsoleNFNormal", coordinates);
            var machine = entities.SpawnEntity("CrateMachine", coordinates);
            var actor = entities.SpawnEntity("MobHumanDummy", coordinates);
            var session = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.PlayerMan.SetAttachedEntity(session, actor);
            var bank = entities.System<BankSystem>();
            var prefs = pair.Server.ResolveDependency<IServerPreferencesManager>().GetPreferences(session.UserId);
            Assert.That(bank.TryBankDeposit(session, prefs, (HumanoidCharacterProfile) prefs.SelectedCharacter,
                30000, out var balance), Is.True);
            bank.SetConfirmedBankBalance(actor, balance.Value);

            entities.EventBus.RaiseLocalEvent(console, new MarketConsoleCartMessage(quantity, listing.Prototype.Id) { Actor = actor });
            Assert.That(market.MarketDataList, Is.Empty);
            var cart = entities.GetComponent<MarketConsoleComponent>(console).CartDataList;
            Assert.That(entities.System<MarketSystem>().CalculateEntityAmount(cart), Is.EqualTo((quantity + 4) / 5));
            var cost = MarketDataExtensions.GetMarketValue(cart, 2f);
            Assert.That(cost, Is.EqualTo(quantity * 3000));

            entities.EventBus.RaiseLocalEvent(console, new MarketPurchaseMessage { Actor = actor });
            Assert.That(entities.GetComponent<BankAccountComponent>(actor).Balance, Is.EqualTo(balance.Value - cost));
            entities.EventBus.RaiseLocalEvent(machine, new CrateMachineOpenedEvent(machine));

            var crate = entities.EntityQuery<EntityStorageComponent, TransformComponent>(true)
                .Single(entry => entry.Item2.GridUid == map.Grid.Owner).Item1;
            var crystals = crate.Contents.ContainedEntities;
            Assert.That(crystals.Select(uid => entities.GetComponent<StackComponent>(uid).Count),
                Is.EquivalentTo(quantity == 5 ? new[] { 5 } : new[] { 5, 2 }));
            Assert.That(crystals.Sum(uid => entities.System<PricingSystem>().GetPrice(uid)), Is.EqualTo(quantity * 1500));

            var resale = new EntitySoldEvent(crystals.ToHashSet(), map.Grid);
            entities.EventBus.RaiseEvent(EventSource.Local, ref resale);
            Assert.That(market.MarketDataList.Single().Quantity, Is.EqualTo(quantity));
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
        });
        await pair.CleanReturnAsync();
    }
}

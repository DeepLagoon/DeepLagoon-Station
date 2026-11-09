using System.Linq;
using System.Numerics;
using Content.Server._NF.Bank;
using Content.Server._NF.CrateMachine;
using Content.Server._NF.Market.Components;
using Content.Server._NF.Market.Extensions;
using Content.Server._NF.Market.Systems;
using Content.Server.Cargo.Systems;
using Content.Server.Preferences.Managers;
using Content.Server.Stack;
using Content.Server.Storage.Components;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Market.Events;
using Content.Shared.Materials;
using Content.Shared.Preferences;
using Content.Shared.Stacks;
using Content.Shared.Station.Components;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class CargoMarketTests
{
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

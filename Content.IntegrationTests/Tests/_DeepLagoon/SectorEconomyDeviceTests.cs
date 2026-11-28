using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Economy;
using Content.Server._NF.Bank;
using Content.Server._NF.SectorServices;
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Nutrition.Components;
using Content.Server.VendingMachines;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.VendingMachines;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class SectorEconomyDeviceTests
{
    [Test]
    public async Task FoodOrdersReserveRealSurplusRefundOnSkipAndRejectEmptyMealsWhilePayrollRemainsAvailable()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            pair.Server.CfgMan.SetCVar(SectorEconomyCVars.Enabled, true);
            var economy = e.System<SectorEconomySystem>();
            var services = e.System<SectorServiceSystem>();
            if (!e.EntityExists(services.GetServiceEntity()))
                e.EnsureComponent<StationSectorServiceHostComponent>(e.SpawnEntity(null, map.GridCoords));
            var bank = e.GetComponent<SectorBankComponent>(services.GetServiceEntity());
            foreach (var account in bank.Accounts.Values) account.Balance = 0;
            var reserve = economy.PayrollReserve(SectorBankAccount.Nfsd);
#pragma warning disable RA0002 // Seed a known treasury balance to test reservations through public bank/bounty operations.
            bank.Accounts[SectorBankAccount.Nfsd].Balance = reserve;
            var cargo = e.System<CargoSystem>();
            var station = e.SpawnEntity(null, map.GridCoords);
            var bounties = e.EnsureComponent<StationCargoBountyDatabaseComponent>(station);
            Assert.That(cargo.TryAddBounty(station, "DLEconomyFoodBurger", bounties), Is.False, "No order without measured food allowance.");
            typeof(SectorEconomySystem).GetField("_foodAllowance", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(economy, 4500m);
            Assert.That(cargo.TryAddBounty(station, "DLEconomyFoodBurger", bounties), Is.False, "Food must not consume protected payroll.");
            bank.Accounts[SectorBankAccount.Nfsd].Balance += 4500;
#pragma warning restore RA0002
            Assert.That(cargo.TryAddBounty(station, "DLEconomyFoodBurger", bounties), Is.True);
            var bounty = bounties.Bounties.Single();
            Assert.That(bounty.FundedReward, Is.EqualTo(4500));
            Assert.That(bounty.FoodAccount, Is.EqualTo(SectorBankAccount.Nfsd));
            Assert.That(bank.Accounts[SectorBankAccount.Nfsd].Balance, Is.EqualTo(reserve));
            Assert.That(e.System<BankSystem>().TrySectorWithdraw(SectorBankAccount.Nfsd, 1, LedgerEntryType.StationWithdrawalOther), Is.False);
            var meals = Enumerable.Range(0, 3).Select(_ => e.SpawnEntity("FoodBurgerPlain", map.GridCoords)).ToHashSet();
            var proto = pair.Server.ResolveDependency<IPrototypeManager>().Index<Content.Shared.Cargo.Prototypes.CargoBountyPrototype>("DLEconomyFoodBurger");
            Assert.That(cargo.IsBountyComplete(new HashSet<EntityUid>(meals), proto.Entries, out _), Is.True);
            var meal = meals.First();
            var solutions = e.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(meal, e.GetComponent<FoodComponent>(meal).Solution, out var solution, out _), Is.True);
            solutions.RemoveAllSolution(solution!.Value);
            Assert.That(cargo.IsBountyComplete(new HashSet<EntityUid>(meals), proto.Entries, out _), Is.False);
            Assert.That(cargo.TryRemoveBounty((station, bounties), bounty, true), Is.True);
            Assert.That(bank.Accounts[SectorBankAccount.Nfsd].Balance, Is.EqualTo(reserve + 4500));
            Assert.That(e.System<BankSystem>().TrySectorWithdraw(SectorBankAccount.Nfsd, 4500, LedgerEntryType.StationWithdrawalPayroll), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExistingPremiumVendorPricesOnlyThePurchasedProductAndUsesTheSameIndexAtEveryLocation()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            pair.Server.CfgMan.SetCVar(SectorEconomyCVars.Enabled, true);
            var economy = e.System<SectorEconomySystem>();
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var first = e.SpawnEntity("FlatpackVendPremium", map.GridCoords);
            var second = e.SpawnEntity("FlatpackVendPremium", map.GridCoords);
            var a = e.GetComponent<VendingMachineComponent>(first); var b = e.GetComponent<VendingMachineComponent>(second);
            var vending = e.System<VendingMachineSystem>();
            var entries = a.Inventory.Values.Take(2).ToArray();
            Assert.That(entries.Length, Is.EqualTo(2));
            var product = prototypes.Index<EntityPrototype>(entries[0].ID);
            var other = prototypes.Index<EntityPrototype>(entries[1].ID);
            var before = vending.GetPurchasePrice(first, product, a);
            var untouched = vending.GetPurchasePrice(first, other, a);
            economy.Purchased("vendor:" + product.ID);
            Assert.That(vending.GetPurchasePrice(first, product, a), Is.GreaterThan(before));
            Assert.That(vending.GetPurchasePrice(second, product, b), Is.EqualTo(vending.GetPurchasePrice(first, product, a)));
            Assert.That(vending.GetPurchasePrice(first, other, a), Is.EqualTo(untouched));
            economy.Purchased("vendor:" + product.ID, 1000000);
            Assert.That(vending.GetPurchasePrice(first, product, a), Is.LessThanOrEqualTo((long)Math.Ceiling(before * 1.4)));
        });
        await pair.CleanReturnAsync();
    }
}

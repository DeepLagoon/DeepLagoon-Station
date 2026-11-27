using System;
using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.Exchange;
using Content.Server._DeepLagoon.CryptoFarm;
using Content.Server._DeepLagoon.Exchange;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server._Mono.Cargo;
using Content.Server.CartridgeLoader;
using Content.Server.Cargo.Systems;
using Content.Shared._DeepLagoon.Exchange;
using Content.Shared.CartridgeLoader;
using Content.Shared.Cargo.Events;
using Content.Shared.PDA;
using Content.Shared.Stacks;
using Content.Shared.Station.Components;
using Robust.Client.UserInterface;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ExchangeTests
{
    [Test]
    public async Task ExchangeFarmCoinsSurviveParkingWithoutBecomingSpessos()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(ExchangeCVars.Enabled, true);
            var farm = e.SpawnEntity("DatafarmCrypto", map.GridCoords);
            var parking = e.System<ParkingSystem>();
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            var farms = e.System<CryptoFarmSystem>();
            var legacy = e.SpawnEntity("BitcoinContribcoin", map.GridCoords);
            e.System<Content.Server.Stack.StackSystem>().SetCount(legacy, 15);
            var restored = parking.LoadSnapshot(parking.FilteredSnapshot(map.Grid));
            var loaded = e.EntityQuery<CryptoFarmComponent, TransformComponent>(true)
                .Single(entry => entry.Item2.GridUid == restored).Item1.Owner;
            Assert.That(e.HasComponent<Content.Shared.Storage.StorageComponent>(loaded), Is.False);
            var coins = e.EntityQuery<DriftingPriceComponent, TransformComponent>(true)
                .Where(entry => entry.Item2.GridUid == restored && !e.IsQueuedForDeletion(entry.Item1.Owner)).ToArray();
            Assert.That(coins, Has.Length.EqualTo(1));
            Assert.That(e.GetComponent<StackComponent>(coins[0].Item1.Owner).Count, Is.EqualTo(15));
            Assert.That(e.System<PricingSystem>().GetPrice(coins[0].Item1.Owner), Is.Zero);
            e.DeleteEntity(restored);
            e.DeleteEntity(map.MapUid);
            config.SetCVar(ExchangeCVars.Enabled, false);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PdaHasTheExchangeAndModernCoinsCannotBeSoldThroughAPalletEvenInsideACrate()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(ExchangeCVars.Enabled, true);
            var coords = map.GridCoords.Offset(new Vector2(.5f, .5f));
            var pda = e.SpawnEntity("ContractorPDA", coords);
            Assert.That(e.System<CartridgeLoaderSystem>().HasProgram<ExchangeCartridgeComponent>(pda, installedOnly: true), Is.True);
            var station = e.SpawnEntity(null, coords);
            e.AddComponent<Content.Server._NF.Market.Components.CargoMarketDataComponent>(station);
            e.EnsureComponent<StationMemberComponent>(map.Grid).Station = station;
            var console = e.SpawnEntity("ComputerPalletConsoleNFVeryHighMarket", coords);
            var pallet = e.SpawnEntity("CargoPalletSell", coords);
            e.System<SharedTransformSystem>().AnchorEntity(console);
            e.System<SharedTransformSystem>().AnchorEntity(pallet);
            var crate = e.SpawnEntity("CrateGenericSteel", coords);
            var coin = e.SpawnEntity("BitcoinContribcoin", coords);
            var saleable = e.SpawnEntity("SheetSteel", coords);
            Assert.That(e.System<PricingSystem>().GetPrice(coin), Is.Zero);
            var storage = e.GetComponent<Content.Server.Storage.Components.EntityStorageComponent>(crate);
            Assert.That(e.System<SharedContainerSystem>().Insert(coin, storage.Contents), Is.True);
            e.EventBus.RaiseLocalEvent(console, new CargoPalletSellMessage());
            Assert.That(e.EntityExists(saleable), Is.False, "The console must still sell ordinary materials.");
            Assert.That(e.EntityExists(coin), Is.True);
            Assert.That(e.EntityExists(crate), Is.True);
            var farm = e.SpawnEntity("DatafarmCrypto", new EntityCoordinates(map.Grid, new Vector2(4, 4)));
            Assert.That(e.HasComponent<Content.Shared.Storage.StorageComponent>(farm), Is.False);
            Assert.That(e.HasComponent<UserInterfaceComponent>(farm), Is.False);
            config.SetCVar(ExchangeCVars.Enabled, false);
            e.DeleteEntity(map.MapUid);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FinancialAccessRequiresTheHoldersOpenPdaAndActiveExchangeProgram()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            var body = e.SpawnEntity("MobHuman", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            var pda = e.SpawnEntity("ContractorPDA", new EntityCoordinates(body, Vector2.Zero));
            var exchange = e.System<ExchangeSystem>();
            Assert.That(exchange.CanUse(session, pda), Is.False, "Holding a closed PDA does not authorize wallet requests.");
            var loader = e.System<CartridgeLoaderSystem>();
            Assert.That(loader.TryGetProgram<ExchangeCartridgeComponent>(pda, out var program, installedOnly: true), Is.True);
            loader.ActivateProgram(pda, program!.Value);
            var ui = e.System<SharedUserInterfaceSystem>();
            ui.OpenUi(pda, PdaUiKey.Key, body);
            Assert.That(exchange.CanUse(session, pda), Is.True);
            var other = e.SpawnEntity("MobHuman", map.GridCoords);
            e.System<SharedTransformSystem>().SetCoordinates(pda, new EntityCoordinates(other, Vector2.Zero));
            Assert.That(exchange.CanUse(session, pda), Is.False, "A PDA held by somebody else cannot access this wallet.");
            ui.CloseUi(pda, PdaUiKey.Key, body);
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            e.DeleteEntity(map.MapUid);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NativeExchangeBuildsBothSectionsAndChartsHandleEmptyFlatAndInvalidHistory()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            using var view = new ExchangeView();
            ui.StateRoot.AddChild(view);
            var points = new[] { new ExchangePoint(0, 10000, 0), new ExchangePoint(60000, 10100, 1000) };
            view.Apply(new ExchangeSnapshot
            {
                Enabled = true, Bank = 75000, At = 60000, Quote = Guid.NewGuid(), BuyCost = 10103,
                Assets = [new("contrib", "CTB", 10100, 1000, 10000, points),
                    new("ideas", "IDEA", 15000, 0, 0, []), new("goida", "GOI", 20000, 0, 0, [])],
                Materials = [new("Steel", 50, 51, [new(0, 50, 0), new(60000, 51, 1000)])],
            });
            var charts = Descendants(view).OfType<ExchangeChart>().ToArray();
            Assert.That(charts, Has.Length.EqualTo(2));
            foreach (var chart in charts)
            {
                chart.Set([], Color.Cyan, false);
                Assert.That(chart.Maximum, Is.GreaterThan(chart.Minimum));
                chart.Set([new(0, 100, 0), new(60000, 100, 0), new(120000, double.NaN, 0)], Color.Cyan, false);
                Assert.That(double.IsFinite(chart.Minimum) && double.IsFinite(chart.Maximum), Is.True);
                Assert.That(chart.Maximum, Is.GreaterThan(chart.Minimum));
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task WalletSetupMasksPasswordsAndSendsOnlyMatchingConfirmation()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Client.WaitAssertion(() =>
        {
            using var view = new ExchangeView();
            pair.Client.ResolveDependency<IUserInterfaceManager>().StateRoot.AddChild(view);
            view.Apply(new ExchangeSnapshot
            {
                Enabled = true, Address = "ABC123", NeedsPassword = true,
                Wallets = [new("ABC123", "1", true)],
                Assets = [new("contrib", "CTB", 10000, 0, 0, []), new("ideas", "IDEA", 15000, 0, 0, []), new("goida", "GOI", 20000, 0, 0, [])],
            });
            Assert.That(Descendants(view).OfType<Robust.Client.UserInterface.Controls.TabContainer>().First().CurrentTab, Is.EqualTo(2));
            var fields = Descendants(view).OfType<Robust.Client.UserInterface.Controls.LineEdit>().Where(c => c.GetType().Name == "PasswordField").ToArray();
            var sent = 0;
            view.Request += (action, _, _, _, _, address, password, _) =>
            {
                Assert.That(action, Is.EqualTo(ExchangeAction.Login));
                Assert.That(address, Is.EqualTo("ABC123"));
                Assert.That(password, Is.EqualTo("secret-password"));
                sent++;
            };
            fields[0].SetText("secret-password", true);
            Assert.That(fields[0].Text, Is.EqualTo(new string('•', 15)));
            fields[1].SetText("wrong-password", true);
            fields[1].ForceSubmitText();
            Assert.That(sent, Is.Zero);
            fields[1].SetText("secret-password", true);
            fields[1].ForceSubmitText();
            Assert.That(sent, Is.EqualTo(1));
            Assert.That(fields.All(c => c.Text.Length == 0), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task WalletSecurityConfirmsTheNewPasswordAndClearsAllSecretFields()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Client.WaitAssertion(() =>
        {
            using var view = new ExchangeView();
            pair.Client.ResolveDependency<IUserInterfaceManager>().StateRoot.AddChild(view);
            view.Apply(new ExchangeSnapshot
            {
                Enabled = true, Address = "ABC123", WalletUnlocked = true, WalletCharacter = "Wallet owner",
                Wallets = [new("ABC123", "1", false)],
                Assets = [new("contrib", "CTB", 10000, 1000, 10000, []), new("ideas", "IDEA", 15000, 0, 0, []), new("goida", "GOI", 20000, 0, 0, [])],
                Withdrawals = [new(0, 9500, "Bank recipient", false, "contrib", 1000)],
            });
            var tabs = Descendants(view).OfType<Robust.Client.UserInterface.Controls.TabContainer>().ToArray();
            Assert.That(tabs, Has.Length.EqualTo(2));
            tabs[0].CurrentTab = 2; tabs[1].CurrentTab = 3;
            var fields = Descendants(view).OfType<Robust.Client.UserInterface.Controls.LineEdit>().ToArray();
            var current = fields.Single(c => c.Name == "WalletCurrentPassword");
            var replacement = fields.Single(c => c.Name == "WalletNewPassword");
            var confirm = fields.Single(c => c.Name == "WalletConfirmPassword");
            var sent = 0;
            view.Request += (action, _, _, _, _, address, password, newPassword) =>
            {
                Assert.That(action, Is.EqualTo(ExchangeAction.ChangePassword));
                Assert.That(address, Is.EqualTo("ABC123"));
                Assert.That(password, Is.EqualTo("current-password"));
                Assert.That(newPassword, Is.EqualTo("replacement-password"));
                sent++;
            };
            current.SetText("current-password", true);
            replacement.SetText("replacement-password", true);
            confirm.SetText("different-password", true); confirm.ForceSubmitText();
            Assert.That(sent, Is.Zero);
            confirm.SetText("replacement-password", true); confirm.ForceSubmitText();
            Assert.That(sent, Is.EqualTo(1));
            Assert.That(new[] { current, replacement, confirm }.All(c => c.Text.Length == 0), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    private static System.Collections.Generic.IEnumerable<Control> Descendants(Control root)
    {
        foreach (var child in root.Children)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}

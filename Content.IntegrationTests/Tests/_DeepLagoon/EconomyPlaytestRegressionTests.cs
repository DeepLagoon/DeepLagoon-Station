using System;
using System.Reflection;
using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.Exchange;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Cargo.UI;
using Content.Client.CartridgeLoader;
using Content.Client._NF.Market.BUI;
using Content.Client._NF.Market.UI;
using Content.Client.PDA;
using Content.Shared.Cargo;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.PDA;
using Content.Shared._NF.Bank.Components;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class EconomyPlaytestRegressionTests
{
    [Test]
    public async Task OrbitalProgramRepeatedLayoutDoesNotInvalidateMeasurementOrResizeItsHost()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            using var host = new PdaOrbitalHost();
            using var program = new Control { MinSize = new Vector2(1000, 1000) };
            host.ProgramLayer.AddChild(program);
            host.Measure(new Vector2(760, 680));
            host.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(760, 680)));
            Assert.That(TguiActionData.TryParse("{\"x\":0.05,\"y\":0.2,\"w\":0.9,\"h\":0.6}", out var viewport), Is.True);
            Assert.That(host.SetViewport(viewport!), Is.True);
            host.LayoutProgram(program, true);
            host.Measure(new Vector2(760, 680));
            host.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(760, 680)));
            program.Measure(new Vector2(760, 680));
            Assert.That(program.IsMeasureValid, Is.True, "Start from a completed native measure pass.");
            var size = program.SetSize;
            for (var frame = 0; frame < 20; frame++)
            {
                host.LayoutProgram(program, true);
                Assert.That(program.SetSize, Is.EqualTo(size));
                Assert.That(program.IsMeasureValid, Is.True, $"An unchanged viewport must not trigger another measure pass each frame ({frame}, host={host.Size}, program={program.Position}, requested={program.SetSize}).");
            }
            host.ProgramLayer.Measure(new Vector2(760, 680));
            Assert.That(host.ProgramLayer.DesiredSize, Is.EqualTo(Vector2.Zero), "The native program must not feed its minimum size back into the browser aperture.");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExistingActivePdaApplicationOpensFromTheApplicationGridWithoutAnotherServerUpdate()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            var old = cfg.GetCVar(WebUiCVars.WindowStyles);
            cfg.SetCVar(WebUiCVars.WindowStyles, WindowInterfaceStyle.WithChoice(old, "Pda", false));
            var entities = pair.Client.EntMan;
            var pda = entities.SpawnEntity("ContractorPDA", MapCoordinates.Nullspace);
            using var bui = new PdaBoundUserInterface(pda, PdaUiKey.Key);
            try
            {
                typeof(PdaBoundUserInterface).GetMethod("CreateMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(bui, null);
                var menu = (PdaMenu) typeof(PdaBoundUserInterface).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bui)!;
                Assert.That(menu.SetSize, Is.EqualTo(new Vector2(760, 680)));
                typeof(CartridgeLoaderBoundUserInterface).GetField("_activeProgram", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bui, pda);
                menu.ToProgramView("Биржа");
                menu.ChangeView(PdaMenu.ProgramListView);
                var click = (Action<EntityUid>) typeof(PdaMenu).GetField("OnProgramItemPressed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(menu)!;
                click(pda);
                Assert.That(menu.ProgramTitle.IsCurrent, Is.True);
                Assert.That(menu.ProgramView.Visible, Is.True);
            }
            finally
            {
                cfg.SetCVar(WebUiCVars.WindowStyles, old);
                entities.DeleteEntity(pda);
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MarketHasOneListingsButtonAndFoodOrderActionsFitTheDefaultConsoleWidth()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var console = entities.SpawnEntity("ComputerMarketConsoleNFNormal", MapCoordinates.Nullspace);
            using var bui = new MarketConsoleBoundUserInterface(console, Content.Shared._NF.Market.MarketConsoleUiKey.Default);
            typeof(MarketConsoleBoundUserInterface).GetMethod("Open", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(bui, null);
            var menu = (MarketMenu) typeof(MarketConsoleBoundUserInterface).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bui)!;
            Assert.That(Descendants(menu).OfType<Button>().Count(button => button.Text == menu.PlayerMarketButton.Text), Is.EqualTo(1));
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var food = prototypes.Index<CargoBountyPrototype>("DLEconomyFoodPizza");
            using var order = new BountyEntry(new CargoBountyData(food, 1) { FundedReward = 4500, FoodAccount = SectorBankAccount.Medical }, TimeSpan.Zero);
            order.Measure(new Vector2(500, float.PositiveInfinity));
            order.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(500, order.DesiredSize.Y)));
            var print = order.FindControl<Button>("PrintButton");
            var skip = order.FindControl<Button>("SkipButton");
            Assert.That(print.Position.X + print.Size.X, Is.LessThanOrEqualTo(skip.Position.X));
            Assert.That(skip.Position.X + skip.Size.X, Is.LessThanOrEqualTo(skip.Parent!.Size.X));
            var funding = order.FindControl<RichTextLabel>("FundingLabel");
            Assert.That(funding.Visible, Is.True);
            Assert.That(funding.GetMessage(), Does.Contain(pair.Client.ResolveDependency<Robust.Shared.Localization.ILocalizationManager>().GetString("ledger-tab-Medical")));
            Assert.That(order.FindControl<RichTextLabel>("RewardLabel").GetMessage(), Does.Not.Contain("VSC$"));
            entities.DeleteEntity(console);
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

    [Test]
    public async Task CargoTelepadCanSwitchFromIdleToBeamRepeatedlyWithoutReentrantDuplicateAnimations()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var pad = entities.SpawnEntity("CargoTelepad", MapCoordinates.Nullspace);
            var appearance = entities.System<AppearanceSystem>();
            var system = entities.System<Content.Client.Cargo.Systems.CargoSystem>();
            var reconcile = typeof(Content.Client.Cargo.Systems.CargoSystem).GetMethod("OnChangeData", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var animations = entities.System<AnimationPlayerSystem>();
            void Set(CargoTelepadState state)
            {
                appearance.SetData(pad, CargoTelepadVisuals.State, state);
                reconcile.Invoke(system, [pad, null]);
            }
            for (var repeat = 0; repeat < 5; repeat++)
            {
                Set(CargoTelepadState.Idle);
                Set(CargoTelepadState.Teleporting);
                Set(CargoTelepadState.Teleporting);
                Assert.That(animations.HasRunningAnimation(pad, "cargo-telepad-beam"), Is.True);
                Set(CargoTelepadState.Unpowered);
                Assert.That(animations.HasRunningAnimation(pad, "cargo-telepad-beam"), Is.False);
                Assert.That(animations.HasRunningAnimation(pad, "cargo-telepad-idle"), Is.False);
            }
            entities.DeleteEntity(pad);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExchangeCartridgeStateRefreshKeepsItsVisibleFragmentAndRecreatesItAfterDisposal()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var pda = entities.SpawnEntity("ContractorPDA", MapCoordinates.Nullspace);
            var bui = new PdaBoundUserInterface(pda, PdaUiKey.Key);
            var fragment = new ExchangeUi();
            fragment.Setup(bui, null);
            var visible = fragment.GetUIFragmentRoot();
            for (var refresh = 0; refresh < 5; refresh++)
            {
                fragment.Setup(bui, null);
                Assert.That(fragment.GetUIFragmentRoot(), Is.SameAs(visible));
                Assert.That(visible.Disposed, Is.False);
            }
            visible.Dispose();
            fragment.Setup(bui, null);
            Assert.That(fragment.GetUIFragmentRoot(), Is.Not.SameAs(visible));
            Assert.That(fragment.GetUIFragmentRoot().Disposed, Is.False);
            fragment.GetUIFragmentRoot().Dispose();
            entities.DeleteEntity(pda);
        });
        await pair.CleanReturnAsync();
    }
}

#nullable enable
using System.Text.Json;
using Content.Client.Power.Battery;
using Content.Shared.Power;
using Robust.Shared.Localization;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class BatteryTguiTests
{
    [Test]
    public async Task BatteryReadingsKeepNativeUnitsEtaAndPrototypeLimits()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var state = new BatteryBuiState
            {
                Charge = 1_250_000, Capacity = 2_500_000, Efficiency = .9f,
                CurrentReceiving = 15_000, CurrentSupply = 5_000,
                CanCharge = true, CanDischarge = false,
                SupplyingNetworkHasPower = true, LoadingNetworkHasPower = false,
                MaxChargeRate = 12_345, MinMaxChargeRate = 2_500, MaxMaxChargeRate = 85_000,
                MaxSupply = 150_000, MinMaxSupply = 10_000, MaxMaxSupply = 450_000,
            };
            using var document = JsonDocument.Parse(BatteryTguiData.Build(state, "test battery", false).ToString());
            var data = document.RootElement;
            Assert.That(data.GetProperty("fraction").GetSingle(), Is.EqualTo(.5f));
            Assert.That(data.GetProperty("passthrough").GetSingle(), Is.EqualTo(5000));
            Assert.That(data.GetProperty("chargeMin").GetSingle(), Is.EqualTo(2500));
            Assert.That(data.GetProperty("chargeMax").GetSingle(), Is.EqualTo(85000));
            Assert.That(data.GetProperty("dischargeMax").GetSingle(), Is.EqualTo(450000));
            Assert.That(data.GetProperty("output").GetBoolean(), Is.False);
            Assert.That(data.GetProperty("inputPowered").GetBoolean(), Is.True);
            Assert.That(data.GetProperty("energyText").GetString(), Is.EqualTo(
                Loc.GetString("battery-menu-stored-energy-value", ("value", state.Charge))));
            Assert.That(data.GetProperty("chargeText").GetString(), Is.EqualTo(
                Loc.GetString("battery-menu-power-value", ("value", 10_000f))));
            Assert.That(data.GetProperty("etaText").GetString(), Is.EqualTo(
                Loc.GetString("battery-menu-eta-value", ("minutes", 3.0))));

            state.CurrentSupply = 25_000;
            using var discharging = JsonDocument.Parse(BatteryTguiData.Build(state, "SMES", true).ToString());
            Assert.That(discharging.RootElement.GetProperty("smes").GetBoolean(), Is.True);
            Assert.That(discharging.RootElement.GetProperty("etaLabel").GetString(), Is.EqualTo(Loc.GetString("battery-menu-eta-empty")));
            Assert.That(discharging.RootElement.GetProperty("dischargeText").GetString(), Is.EqualTo(
                Loc.GetString("battery-menu-power-value", ("value", 10_000f))));

            state.CurrentSupply = state.CurrentReceiving;
            state.Capacity = state.Charge = 0;
            using var empty = JsonDocument.Parse(BatteryTguiData.Build(state, "empty battery", false).ToString());
            Assert.That(empty.RootElement.GetProperty("fraction").GetSingle(), Is.Zero);
            Assert.That(empty.RootElement.GetProperty("etaText").GetString(), Is.EqualTo(Loc.GetString("battery-menu-eta-value-na")));
            using var waiting = JsonDocument.Parse(BatteryTguiData.Build(null, "loading", false).ToString());
            Assert.That(waiting.RootElement.GetProperty("ready").GetBoolean(), Is.False);
        });
        await pair.CleanReturnAsync();
    }
}

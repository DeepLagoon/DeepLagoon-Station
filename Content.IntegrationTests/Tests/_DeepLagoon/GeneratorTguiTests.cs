#nullable enable
using System.Text.Json;
using Content.Client.Power.Generator;
using Content.Shared.Power.Generator;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class GeneratorTguiTests
{
    [Test]
    public async Task GeneratorPanelRetainsNativeReadingsAndInheritedFuelFinishes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var fuel = (FuelGeneratorComponent) prototypes.Index<EntityPrototype>("PortableGeneratorSuperPacman").Components["FuelGenerator"].Component;
            var state = new PortableGeneratorComponentBuiState(fuel, 7.65f, true, (7200, 2700))
            { TargetPower = 30000, OptimalPower = 30000, MaximumPower = 60000, On = true };
            using var document = JsonDocument.Parse(GeneratorTguiData.Build(state, fuel, "test", "uranium",
                "dl-generator-fuel-uranium", true, true, .4f).ToString());
            var data = document.RootElement;
            Assert.That(data.GetProperty("target").GetSingle(), Is.EqualTo(30));
            Assert.That(data.GetProperty("min").GetSingle(), Is.EqualTo(5));
            Assert.That(data.GetProperty("max").GetSingle(), Is.EqualTo(60));
            Assert.That(data.GetProperty("fuelFraction").GetSingle(), Is.EqualTo(.65f).Within(.0001));
            Assert.That(data.GetProperty("fuelText").GetString(), Is.EqualTo("7"));
            Assert.That(data.GetProperty("clogged").GetBoolean(), Is.True);
            Assert.That(data.GetProperty("startProgress").GetSingle(), Is.EqualTo(.4f));
            Assert.That(data.GetProperty("networkGood").GetBoolean(), Is.False);
            Assert.That(data.GetProperty("efficiencyText").GetString(), Is.EqualTo(1f.ToString("P1")));
            Assert.That(data.GetProperty("etaText").GetString(), Is.EqualTo(Loc.GetString("portable-generator-ui-eta", ("minutes", 12.0))));
            Assert.That(data.GetProperty("networkText").GetString(), Is.EqualTo(Loc.GetString(
                "portable-generator-ui-network-stats-value", ("load", 7200f), ("supply", 2700f))));
            state.TargetPower = 0;
            using var idle = JsonDocument.Parse(GeneratorTguiData.Build(state, fuel, "test", "steel",
                "dl-generator-fuel-generic", false, false, 0).ToString());
            Assert.That(idle.RootElement.GetProperty("etaText").GetString(), Is.EqualTo("—"));
            using var waiting = JsonDocument.Parse(GeneratorTguiData.Build(null, null, "test", "steel",
                "dl-generator-fuel-generic", false, false, 0).ToString());
            Assert.That(waiting.RootElement.GetProperty("ready").GetBoolean(), Is.False);

            foreach (var (id, style, label) in new[]
                     {
                         ("PortableGeneratorPacmanShuttle", "plasma", "plasma"),
                         ("PortableGeneratorSuperPacmanShuttle", "uranium", "uranium"),
                         ("PortableGeneratorJrPacmanShuttle", "welding", "welding"),
                         ("PortableGeneratorDKShuttle", "bananium", "bananium"),
                         ("PortableGeneratorDKJrShuttle", "bananium-jr", "bananium"),
                         ("GeneratorCRPinchShuttle", "pinch", "plasma"),
                         ("GeneratorSterlingPDVShuttle", "sterling", "plasma"),
                     })
            {
                var portable = (PortableGeneratorComponent) prototypes.Index<EntityPrototype>(id).Components["PortableGenerator"].Component;
                Assert.That(portable.PanelStyle, Is.EqualTo(style), id);
                Assert.That(portable.PanelFuel, Is.EqualTo("dl-generator-fuel-" + label), id);
            }
        });
        await pair.CleanReturnAsync();
    }
}

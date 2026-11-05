using System.Linq;
using System.Numerics;
using Content.Client.Botany.UI;
using Content.Client.Chemistry.UI;
using Content.Shared.Botany;
using Content.Shared.Botany.Components;
using Content.Shared.Chemistry.Reagent;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Botany;

[TestFixture]
public sealed class PlantAnalyzerWindowTests
{
    [Test]
    public async Task ReagentViewportHasRoomForRowsWithManyMutations()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var plant = entities.SpawnEntity("AmbrosiaDeusPlants", MapCoordinates.Nullspace);
            try
            {
                const int reagentCount = 128;
                var reagents = pair.Client.ResolveDependency<IPrototypeManager>().EnumeratePrototypes<ReagentPrototype>()
                    .Take(reagentCount)
                    .ToDictionary(x => new ProtoId<ReagentPrototype>(x.ID), _ => new PlantChemQuantity { Min = 1, Max = 10, PotencyDivisor = 10 });
                var component = entities.GetComponent<PlantChemicalsComponent>(plant);
                typeof(PlantChemicalsComponent).GetField(nameof(PlantChemicalsComponent.Chemicals))!.SetValue(component, reagents);

                using var window = new PlantAnalyzerWindow();
                window.OpenCentered();
                window.Update(new BotanyAnalyzerState
                {
                    Target = entities.GetNetEntity(plant),
                    Plant = entities.GetNetEntity(plant),
                    PlantProtoId = "AmbrosiaDeusPlants",
                });
                var size = new Vector2(750, 500);
                window.Measure(size);
                window.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));

                var chemicals = window.FindControl<ReagentDisplayControl>("Chemicals");
                Assert.That(chemicals.ChildCount, Is.EqualTo(reagentCount));
                Assert.That(chemicals.Parent, Is.TypeOf<ScrollContainer>());
                Assert.That(chemicals.Parent!.Size.Y, Is.GreaterThanOrEqualTo(chemicals.GetChild(0).Size.Y));
                Assert.That(chemicals.GetChild(0).Size.Y, Is.Positive);
                window.Close();
            }
            finally
            {
                entities.DeleteEntity(plant);
            }
        });
        await pair.CleanReturnAsync();
    }
}

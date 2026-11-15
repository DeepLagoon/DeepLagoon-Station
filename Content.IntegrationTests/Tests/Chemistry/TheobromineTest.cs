using System.Linq;
using Content.Server.Body.Systems;
using Content.Server.EntityEffects.Effects;
using Content.Shared._Shitmed.Body.Organ;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.EntityEffects;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Chemistry;

[TestFixture]
public sealed class TheobromineTest
{
    private static readonly ProtoId<ReagentPrototype> Theobromine = "Theobromine";

    [TestCase("MobTajaran", false)]
    [TestCase("MobFelinid", true)]
    [TestCase("MobMouse", true)]
    [TestCase("MobHuman", false)]
    public async Task OnlySusceptibleSpeciesSufferTheobromineEffects(string mobPrototype, bool susceptible)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var reagent = prototypes.Index(Theobromine);
            var mob = entities.SpawnEntity(mobPrototype, map.GridCoords);
            var heart = entities.System<BodySystem>().GetBodyOrganEntityComps<HeartComponent>(mob).Single();
            var args = new EntityEffectReagentArgs(mob, entities, heart.Owner, null, 1, reagent, null, 1f);
            var effects = reagent.Metabolisms["Poison"].Effects;
            var damage = effects.OfType<HealthChange>().Single();
            var vomit = effects.OfType<ChemVomit>().Single();

            // Evaluate the conditions regardless of the random roll for vomiting.
            Assert.That(damage.Conditions?.All(condition => condition.Condition(args)) ?? true, Is.EqualTo(susceptible));
            Assert.That(vomit.Conditions?.All(condition => condition.Condition(args)) ?? true, Is.EqualTo(susceptible));
            entities.DeleteEntity(mob);
        });
        await pair.CleanReturnAsync();
    }
}

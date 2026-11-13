using System.Linq;
using System.Numerics;
using Content.Server._Goobstation.Temperature;
using Content.Server._DeepLagoon.Species.Hydrakin;
using Content.Server.Power.EntitySystems;
using Content.Server.Temperature.Components;
using Content.Server.Temperature.Systems;
using Content.Shared.Atmos;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Power.Components;
using Content.Shared.Temperature.Components;
using Content.Shared.Verbs;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class HydrakinUniformCoolingTests
{
    [Test]
    public async Task CoolingRequiresAChargedModuleInTheHydrakinUniform()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var map = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;

        await pair.Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            var wearer = entities.SpawnEntity("MobHydrakin", coords);
            var human = entities.SpawnEntity("MobHuman", coords.Offset(Vector2.UnitX));
            var uniform = entities.SpawnEntity("ClothingUniformJumpsuitColorGrey", coords);
            var module = entities.SpawnEntity("DLHydrakinUniformCooling", coords);
            var cell = entities.SpawnEntity("PowerCellSmall", coords);
            var battery = entities.GetComponent<BatteryComponent>(cell);
            var temperature = entities.GetComponent<TemperatureComponent>(wearer);
            var hands = entities.System<SharedHandsSystem>();
            var interact = entities.System<SharedInteractionSystem>();
            var inventory = entities.System<InventorySystem>();
            var cooling = entities.System<HydrakinUniformCoolingSystem>();
            var verbs = entities.System<SharedVerbSystem>();

            Assert.That(hands.TryPickupAnyHand(wearer, module, animate: false), Is.True);
            Assert.That(interact.InteractUsing(wearer, module, uniform, coords), Is.True);
            Assert.That(entities.GetComponent<TransformComponent>(module).ParentUid, Is.EqualTo(uniform));
            Assert.That(inventory.TryEquip(wearer, uniform, "jumpsuit", force: true), Is.True);
            temperature.CurrentTemperature = 330f;
            cooling.Update(1f);
            Assert.That(temperature.CurrentTemperature, Is.EqualTo(330f), "A module without a cell cannot cool");

            Assert.That(inventory.TryUnequip(wearer, "jumpsuit", force: true), Is.True);
            var remove = verbs.GetLocalVerbs(uniform, wearer, typeof(InteractionVerb))
                .Single(verb => verb.Text == Loc.GetString("hydrakin-uniform-cooling-remove"));
            verbs.ExecuteVerb(remove, wearer, uniform);
            Assert.That(hands.IsHolding(wearer, module, out _), Is.True);
            Assert.That(hands.TryPickupAnyHand(wearer, cell, animate: false), Is.True);
            Assert.That(interact.InteractUsing(wearer, cell, module, coords), Is.True);
            Assert.That(entities.GetComponent<TransformComponent>(cell).ParentUid, Is.EqualTo(module));
            Assert.That(interact.InteractUsing(wearer, module, uniform, coords), Is.True);

            Assert.That(inventory.TryEquip(human, uniform, "jumpsuit", force: true), Is.True);
            cooling.Update(1f);
            Assert.That(battery.CurrentCharge, Is.EqualTo(battery.MaxCharge), "The module does not run for humans");
            Assert.That(inventory.TryUnequip(human, "jumpsuit", force: true), Is.True);
            Assert.That(inventory.TryEquip(wearer, uniform, "jumpsuit", force: true), Is.True);

            cooling.Update(1f);
            Assert.That(temperature.CurrentTemperature, Is.LessThan(330f));
            Assert.That(battery.CurrentCharge, Is.LessThan(battery.MaxCharge));
            for (var second = 1; second < 1199; second++)
                cooling.Update(1f);

            Assert.That(temperature.CurrentTemperature, Is.EqualTo(Atmospherics.T20C).Within(0.01f));
            Assert.That(battery.CurrentCharge, Is.GreaterThan(0f));
            cooling.Update(1f);
            Assert.That(battery.CurrentCharge, Is.EqualTo(0f).Within(0.01f));
            cooling.Update(1f);

            temperature.CurrentTemperature = 330f;
            cooling.Update(1f);
            Assert.That(temperature.CurrentTemperature, Is.EqualTo(330f), "An exhausted cell cannot cool");
        });

        await pair.CleanReturnAsync();
    }

    [TestCase("MobSynth")]
    [TestCase("MobIPC")]
    public async Task ChargedUniformPreventsSiliconOverheat(string wearerPrototype)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var map = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;

        await pair.Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            var wearer = entities.SpawnEntity(wearerPrototype, coords);
            var uniform = entities.SpawnEntity("ClothingUniformJumpsuitColorGrey", coords);
            var module = entities.SpawnEntity("DLHydrakinUniformCooling", coords);
            var cell = entities.SpawnEntity("PowerCellSmall", coords);
            var battery = entities.GetComponent<BatteryComponent>(cell);
            var temperature = entities.GetComponent<TemperatureComponent>(wearer);
            var hands = entities.System<SharedHandsSystem>();
            var interact = entities.System<SharedInteractionSystem>();
            var inventory = entities.System<InventorySystem>();
            var cooling = entities.System<HydrakinUniformCoolingSystem>();
            var overheat = entities.System<SlowOnOverheatSystem>();
            var temperatures = entities.System<TemperatureSystem>();
            var threshold = entities.GetComponent<SlowOnOverheatComponent>(wearer).OverheatThreshold;
            var extremeHeat = temperature.HeatDamageThreshold + 100f;

            Assert.That(hands.TryPickupAnyHand(wearer, cell, animate: false), Is.True);
            Assert.That(interact.InteractUsing(wearer, cell, module, coords), Is.True);
            Assert.That(hands.TryPickupAnyHand(wearer, module, animate: false), Is.True);
            Assert.That(interact.InteractUsing(wearer, module, uniform, coords), Is.True);

            temperatures.ForceChangeTemperature(wearer, extremeHeat);
            overheat.Update(0f);
            Assert.That(entities.HasComponent<OverheatSlowdownComponent>(wearer), Is.True);

            Assert.That(inventory.TryEquip(wearer, uniform, "jumpsuit", force: true), Is.True);
            cooling.Update(1f);
            overheat.Update(0f);
            Assert.That(temperature.CurrentTemperature, Is.LessThan(threshold));
            Assert.That(entities.HasComponent<OverheatSlowdownComponent>(wearer), Is.False);
            Assert.That(battery.CurrentCharge, Is.LessThan(battery.MaxCharge));

            temperatures.ChangeHeat(wearer, extremeHeat * temperatures.GetHeatCapacity(wearer), ignoreHeatResistance: true);
            Assert.That(temperature.CurrentTemperature, Is.LessThan(threshold));
            temperatures.ForceChangeTemperature(wearer, extremeHeat);
            Assert.That(temperature.CurrentTemperature, Is.LessThan(threshold));

            temperatures.ForceChangeTemperature(wearer, threshold - 20f);
            cooling.Update(1f);
            Assert.That(temperature.CurrentTemperature, Is.LessThan(threshold - 20f));

            entities.System<BatterySystem>().SetCharge(cell, 0f);
            temperatures.ForceChangeTemperature(wearer, extremeHeat);
            cooling.Update(1f);
            overheat.Update(0f);
            Assert.That(temperature.CurrentTemperature, Is.EqualTo(extremeHeat));
            Assert.That(entities.HasComponent<OverheatSlowdownComponent>(wearer), Is.True);
        });

        await pair.CleanReturnAsync();
    }

    [TestCase("PowerCellMicroreactor")]
    [TestCase("PowerCellAntiqueProto")]
    public async Task SelfChargingCellsSustainCooling(string cellPrototype)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var map = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;

        await pair.Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            var wearer = entities.SpawnEntity("MobHydrakin", coords);
            var uniform = entities.SpawnEntity("ClothingUniformJumpsuitColorGrey", coords);
            var module = entities.SpawnEntity("DLHydrakinUniformCooling", coords);
            var cell = entities.SpawnEntity(cellPrototype, coords);
            var battery = entities.GetComponent<BatteryComponent>(cell);
            var hands = entities.System<SharedHandsSystem>();
            var interact = entities.System<SharedInteractionSystem>();
            var cooling = entities.System<HydrakinUniformCoolingSystem>();
            var batteries = entities.System<BatterySystem>();

            Assert.That(hands.TryPickupAnyHand(wearer, cell, animate: false), Is.True);
            Assert.That(interact.InteractUsing(wearer, cell, module, coords), Is.True);
            Assert.That(hands.TryPickupAnyHand(wearer, module, animate: false), Is.True);
            Assert.That(interact.InteractUsing(wearer, module, uniform, coords), Is.True);
            Assert.That(entities.System<InventorySystem>().TryEquip(wearer, uniform, "jumpsuit", force: true), Is.True);

            cooling.Update(1f);
            Assert.That(battery.CurrentCharge, Is.LessThan(battery.MaxCharge));
            batteries.Update(1f);
            Assert.That(battery.CurrentCharge, Is.EqualTo(battery.MaxCharge));
        });

        await pair.CleanReturnAsync();
    }
}

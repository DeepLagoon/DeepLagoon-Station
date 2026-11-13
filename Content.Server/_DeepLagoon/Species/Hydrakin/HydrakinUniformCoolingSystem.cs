using Content.Server._Goobstation.Temperature;
using Content.Server.PowerCell;
using Content.Server.Temperature;
using Content.Server.Temperature.Components;
using Content.Server.Temperature.Systems;
using Content.Shared._Obelisk.Species.Components;
using Content.Shared.Clothing.Components;
using Content.Shared.Examine;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Content.Shared.Power.Components;
using Content.Shared.Verbs;
using Robust.Shared.Containers;

namespace Content.Server._DeepLagoon.Species.Hydrakin;

public sealed partial class HydrakinUniformCoolingSystem : EntitySystem
{
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private PowerCellSystem _powerCells = default!;
    [Dependency] private TemperatureSystem _temperature = default!;

    private const string ModuleSlot = "hydrakin-uniform-cooling";
    private const string UniformSlot = "jumpsuit";
    private float _elapsed;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<HydrakinUniformCoolingComponent, AfterInteractEvent>(OnInstall);
        SubscribeLocalEvent<ClothingComponent, GetVerbsEvent<InteractionVerb>>(OnUniformVerbs);
        SubscribeLocalEvent<ClothingComponent, ExaminedEvent>(OnUniformExamined);
        SubscribeLocalEvent<SlowOnOverheatComponent, LimitTemperatureIncreaseEvent>(OnLimitTemperatureIncrease);
    }

    private EntityUid? GetModule(EntityUid uniform)
    {
        return _containers.TryGetContainer(uniform, ModuleSlot, out var container) && container is ContainerSlot slot
            ? slot.ContainedEntity
            : null;
    }

    private void OnInstall(Entity<HydrakinUniformCoolingComponent> ent, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target is not { } uniform ||
            !TryComp<ClothingComponent>(uniform, out var clothing) || (clothing.Slots & SlotFlags.INNERCLOTHING) == 0)
            return;

        args.Handled = true;
        if (GetModule(uniform) != null)
        {
            _popup.PopupEntity(Loc.GetString("hydrakin-uniform-cooling-already-installed"), uniform, args.User);
            return;
        }

        var slot = _containers.EnsureContainer<ContainerSlot>(uniform, ModuleSlot);
        if (_hands.TryDropIntoContainer(args.User, ent, slot))
            _popup.PopupEntity(Loc.GetString("hydrakin-uniform-cooling-installed"), uniform, args.User);
    }

    private void OnUniformVerbs(Entity<ClothingComponent> ent, ref GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.Hands == null || GetModule(ent) is not { } module)
            return;

        var user = args.User;
        args.Verbs.Add(new InteractionVerb
        {
            Text = Loc.GetString("hydrakin-uniform-cooling-remove"),
            Act = () => _hands.TryPickupAnyHand(user, module),
        });
    }

    private void OnUniformExamined(Entity<ClothingComponent> ent, ref ExaminedEvent args)
    {
        if (GetModule(ent) is not { } module)
            return;

        args.PushMarkup(Loc.GetString("hydrakin-uniform-cooling-examine"));
        _powerCells.TryGetBatteryFromSlot(module, out var battery);
        _powerCells.OnBatteryExamined(module, battery, args);
    }

    private (EntityUid Uid, HydrakinUniformCoolingComponent Cooling, BatteryComponent Battery)? GetPoweredModule(EntityUid wearer)
    {
        if (!_inventory.TryGetSlotEntity(wearer, UniformSlot, out var uniform) ||
            GetModule(uniform.Value) is not { } module ||
            !TryComp<HydrakinUniformCoolingComponent>(module, out var cooling) ||
            _mobState.IsDead(wearer) ||
            !_powerCells.TryGetBatteryFromSlot(module, out var battery) ||
            battery.CurrentCharge <= 0f)
            return null;

        return (module, cooling, battery);
    }

    private static float GetSafeTemperature(SlowOnOverheatComponent overheat, TemperatureComponent temperature)
    {
        var damageThreshold = temperature.ParentHeatDamageThreshold ?? temperature.HeatDamageThreshold;
        return Math.Min(overheat.OverheatThreshold, damageThreshold) - 1f;
    }

    private void OnLimitTemperatureIncrease(Entity<SlowOnOverheatComponent> ent, ref LimitTemperatureIncreaseEvent args)
    {
        if (!TryComp<TemperatureComponent>(ent, out var temperature))
            return;

        var limit = Math.Max(temperature.CurrentTemperature, GetSafeTemperature(ent.Comp, temperature));
        if (args.Temperature > limit && GetPoweredModule(ent) != null)
            args.Temperature = limit;
    }

    public override void Update(float frameTime)
    {
        _elapsed += frameTime;
        if (_elapsed < 1f)
            return;

        var elapsed = _elapsed;
        _elapsed = 0f;

        var query = EntityQueryEnumerator<InventoryComponent, TemperatureComponent>();
        while (query.MoveNext(out var wearer, out _, out var temperature))
        {
            if (!TryComp<SlowOnOverheatComponent>(wearer, out var overheat) && !HasComp<HydrakinComponent>(wearer))
                continue;

            if (GetPoweredModule(wearer) is not { } powered)
                continue;

            // The circuit stays powered while worn, including between cooling cycles.
            var requiredCharge = powered.Battery.MaxCharge * elapsed / (float) powered.Cooling.BatteryRuntime.TotalSeconds;
            var usedCharge = Math.Min(powered.Battery.CurrentCharge, requiredCharge);
            if (!_powerCells.TryUseCharge(powered.Uid, usedCharge))
                continue;

            var coolingTime = elapsed * (usedCharge / requiredCharge);
            var current = temperature.CurrentTemperature;
            var target = Math.Max(powered.Cooling.TargetTemperature, current - powered.Cooling.CoolingRate * coolingTime);
            if (overheat != null)
                target = Math.Min(target, GetSafeTemperature(overheat, temperature));

            if (target >= current)
                continue;

            var heatCapacity = _temperature.GetHeatCapacity(wearer, temperature);
            _temperature.ChangeHeat(wearer, (target - current) * heatCapacity, ignoreHeatResistance: true, temperature);
        }
    }
}

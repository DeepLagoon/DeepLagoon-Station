using System.Linq;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server.Lathe.Components;
using Content.Server.Lathe;
using Content.Server.Power.EntitySystems;
using Content.Server.Research.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Stack;
using Content.Shared._DeepLagoon.Economy;
using Content.Shared._Goobstation.ItemMiner;
using Content.Shared.Examine;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Research.Components;
using Content.Shared.Stacks;
using Content.Shared.Tools.Systems;
using Content.Shared.Verbs;
using Robust.Shared.Timing;
using Robust.Shared.Configuration;

namespace Content.Server._DeepLagoon.Economy;

/// <summary>Saved with the equipment; no wall-clock or parked wear.</summary>
[RegisterComponent]
public sealed partial class EquipmentMaintenanceComponent : Component
{
    [DataField] public double WorkingSeconds;
    [DataField] public float AppliedThrustFactor = 1;
    [DataField] public float AppliedLatheFactor = 1;
    [DataField] public float AppliedMinerFactor = 1;
}

public sealed class EquipmentMaintenanceSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedToolSystem _tools = default!;
    [Dependency] private StackSystem _stacks = default!;
    [Dependency] private IGameTiming _timing = default!;
    private TimeSpan _next;
    private float _elapsed;
    public bool Enabled => _config.GetCVar(SectorEconomyCVars.MaintenanceEnabled);

    public override void Initialize()
    {
        SubscribeLocalEvent<EquipmentMaintenanceComponent, ExaminedEvent>(OnExamine);
        SubscribeLocalEvent<EquipmentMaintenanceComponent, GetVerbsEvent<InteractionVerb>>(OnVerbs);
        SubscribeLocalEvent<EquipmentMaintenanceComponent, MaintenanceFinishedEvent>(OnFinished);
        _config.OnValueChanged(SectorEconomyCVars.MaintenanceEnabled, OnEnabled);
    }
    public override void Shutdown() => _config.UnsubValueChanged(SectorEconomyCVars.MaintenanceEnabled, OnEnabled);
    private void OnEnabled(bool enabled)
    {
        _elapsed = 0;
        _next = _timing.CurTime;
        // Update active and parked equipment immediately; retain its saved wear counter.
        var query = EntityManager.AllEntityQueryEnumerator<EquipmentMaintenanceComponent>();
        while (query.MoveNext(out var uid, out var comp)) Refresh(uid, comp);
    }

    public static float Factor(double workingSeconds) => 1 - .2f * (float)Math.Clamp((workingSeconds - 14400) / 14400, 0, 1);
    private bool Eligible(EntityUid uid) => Enabled && Transform(uid).GridUid is { } grid &&
        TryComp<ParkingShuttleComponent>(grid, out var ship) && !ship.Parked && ship.OwnerAccount != Guid.Empty;
    public float Efficiency(EntityUid uid) => Eligible(uid) && TryComp<EquipmentMaintenanceComponent>(uid, out var comp)
        ? Factor(comp.WorkingSeconds) : 1;
    public void Work(EntityUid uid, double seconds)
    {
        if (!Eligible(uid) || !double.IsFinite(seconds) || seconds <= 0) return;
        var comp = EnsureComp<EquipmentMaintenanceComponent>(uid);
        comp.WorkingSeconds = Math.Min(28800, comp.WorkingSeconds + Math.Min(seconds, 10));
    }
    public TimeSpan ProductionTime(EntityUid uid, TimeSpan duration)
    {
        var factor = Efficiency(uid);
        if (TryComp<EquipmentMaintenanceComponent>(uid, out var comp)) comp.AppliedLatheFactor = factor;
        return duration / factor;
    }
    public void TrackMinerFactor(EntityUid uid)
    {
        if (TryComp<EquipmentMaintenanceComponent>(uid, out var comp)) comp.AppliedMinerFactor = Efficiency(uid);
    }

    public override void Update(float frameTime)
    {
        if (!Enabled) return;
        _elapsed += Math.Clamp(frameTime, 0, 1);
        if (_timing.CurTime < _next) return;
        _next = _timing.CurTime + TimeSpan.FromSeconds(10);
        var elapsed = Math.Min(10, _elapsed);
        _elapsed = 0;
        var lathes = EntityQueryEnumerator<LatheProducingComponent>();
        while (lathes.MoveNext(out var uid, out _)) Work(uid, elapsed);
        var sources = EntityQueryEnumerator<ResearchPointSourceComponent, ResearchClientComponent>();
        while (sources.MoveNext(out var uid, out var source, out var client))
            if (source.Active && client.Server != null && this.IsPowered(uid, EntityManager)) Work(uid, elapsed);
        var thrusters = EntityQueryEnumerator<ThrusterComponent>();
        while (thrusters.MoveNext(out var uid, out var thruster))
        {
            if (thruster.Firing && thruster.IsOn) Work(uid, elapsed);
            if (TryComp<EquipmentMaintenanceComponent>(uid, out var comp)) RefreshThrust(uid, comp);
        }
    }

    private void RefreshThrust(EntityUid uid, EquipmentMaintenanceComponent comp)
    {
        var factor = Efficiency(uid);
        if (Math.Abs(factor - comp.AppliedThrustFactor) < .00001f) return;
        EntityManager.System<ThrusterSystem>().ApplyMaintenance(uid, comp.AppliedThrustFactor, factor);
        comp.AppliedThrustFactor = factor;
    }
    private void Refresh(EntityUid uid, EquipmentMaintenanceComponent comp)
    {
        var factor = Efficiency(uid);
        if (HasComp<ThrusterComponent>(uid)) RefreshThrust(uid, comp);
        if (TryComp<LatheProducingComponent>(uid, out var lathe) && Math.Abs(factor - comp.AppliedLatheFactor) > .00001f)
        {
            var elapsed = _timing.CurTime - lathe.StartTime;
            var remaining = lathe.ProductionLength - elapsed;
            if (remaining > TimeSpan.Zero) lathe.ProductionLength = elapsed + remaining * comp.AppliedLatheFactor / factor;
            EntityManager.System<LatheSystem>().UpdateUserInterfaceState(uid);
        }
        comp.AppliedLatheFactor = factor;
        if (TryComp<ItemMinerComponent>(uid, out var miner) && miner.NextAt > _timing.CurTime &&
            Math.Abs(factor - comp.AppliedMinerFactor) > .00001f)
            miner.NextAt = _timing.CurTime + (miner.NextAt - _timing.CurTime) * comp.AppliedMinerFactor / factor;
        comp.AppliedMinerFactor = factor;
    }
    private void OnExamine(Entity<EquipmentMaintenanceComponent> ent, ref ExaminedEvent args)
    {
        if (!Enabled || !args.IsInDetailsRange) return;
        args.PushMarkup(Loc.GetString("dl-economy-maintenance-status", ("efficiency", (int)Math.Round(Efficiency(ent) * 100))));
    }
    private EntityUid? Materials(EntityUid user) => _hands.EnumerateHands(user).Select(h => h.HeldEntity)
        .FirstOrDefault(uid => uid != null && TryComp<StackComponent>(uid, out var stack) && stack.Count >= 2 &&
            stack.StackTypeId.Id is "Steel" or "Cable");
    private void OnVerbs(Entity<EquipmentMaintenanceComponent> ent, ref GetVerbsEvent<InteractionVerb> args)
    {
        if (!Eligible(ent) || ent.Comp.WorkingSeconds < 3600 || !args.CanAccess || !args.CanInteract || args.Using is not { } tool ||
            !_tools.HasQuality(tool, "Screwing")) return;
        var user = args.User;
        args.Verbs.Add(new InteractionVerb { Text = Loc.GetString("dl-economy-maintenance-service"),
            Act = () => { if (Materials(user) != null) _tools.UseTool(tool, user, ent, 5, "Screwing", new MaintenanceFinishedEvent()); } });
    }
    private void OnFinished(Entity<EquipmentMaintenanceComponent> ent, ref MaintenanceFinishedEvent args)
    {
        if (args.Cancelled || args.Handled || !Eligible(ent) || ent.Comp.WorkingSeconds < 3600 || args.Used is not { } tool ||
            !_hands.IsHolding(args.User, tool, out _) || !_tools.HasQuality(tool, "Screwing") ||
            !_interaction.InRangeUnobstructed(args.User, ent.Owner) || Materials(args.User) is not { } materials) return;
        if (!_stacks.Use(materials, 2)) return;
        args.Handled = true;
        ent.Comp.WorkingSeconds = 0;
        Refresh(ent, ent.Comp);
    }
}

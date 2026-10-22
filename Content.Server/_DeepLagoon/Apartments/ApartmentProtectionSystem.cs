using System.Linq;
using Content.Server._NF.PacifiedZone;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Tiles;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.Apartments;

/// <summary>Adapts the existing grid protection and pacified zones to residential grids.</summary>
public sealed class ApartmentProtectionSystem : EntitySystem
{
    private sealed record State(bool AddedProtection, bool FloorRemoval, bool FloorPlacement,
        bool Rcd, bool Emp, bool Explosions, bool Artifacts, bool Hostiles,
        bool AddedZone, bool WholeGrid, bool Combat, List<ProtoId<JobPrototype>> ImmuneRoles, TimeSpan? ImmunePlaytime);

    private readonly Dictionary<EntityUid, State> _states = new();
    private readonly HashSet<EntityUid> _stops = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ApartmentGridComponent, ComponentStartup>((uid, comp, args) => EnsureComp<ApartmentProtectedGridComponent>(uid));
        SubscribeLocalEvent<ApartmentProtectedGridComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<ApartmentProtectedGridComponent, ComponentShutdown>(OnShutdown);
    }

    public void SetStops(HashSet<EntityUid> grids)
    {
        foreach (var grid in grids)
        {
            // A frozen map may already own permanent residential protection.
            if (!HasComp<ApartmentProtectedGridComponent>(grid))
            {
                EnsureComp<ApartmentProtectedGridComponent>(grid);
                _stops.Add(grid);
            }
        }
        foreach (var grid in _stops.Where(grid => !grids.Contains(grid)).ToArray())
        {
            _stops.Remove(grid);
            if (!Deleted(grid)) RemComp<ApartmentProtectedGridComponent>(grid);
        }
    }

    private void OnStartup(EntityUid uid, ApartmentProtectedGridComponent comp, ComponentStartup args)
    {
        var addedProtection = !TryComp<ProtectedGridComponent>(uid, out var protection);
        protection ??= EnsureComp<ProtectedGridComponent>(uid);
        var addedZone = !TryComp<PacifiedZoneGeneratorComponent>(uid, out var zone);
        _states[uid] = new State(addedProtection, protection.PreventFloorRemoval, protection.PreventFloorPlacement,
            protection.PreventRCDUse, protection.PreventEmpEvents, protection.PreventExplosions,
            protection.PreventArtifactTriggers, protection.KillHostileMobs,
            addedZone, zone?.WholeGrid ?? false, zone?.DisallowAllCombat ?? false,
            zone?.ImmuneRoles.ToList() ?? new(), zone?.ImmunePlaytime);

        protection.PreventFloorRemoval = protection.PreventFloorPlacement = protection.PreventRCDUse = true;
        protection.PreventEmpEvents = protection.PreventExplosions = protection.PreventArtifactTriggers = true;
        protection.KillHostileMobs = true;
        Dirty(uid, protection);
        if (addedZone)
            AddComp(uid, new PacifiedZoneGeneratorComponent { WholeGrid = true, DisallowAllCombat = true });
        else
        {
            zone!.WholeGrid = true;
            zone.DisallowAllCombat = true;
        }
        zone ??= Comp<PacifiedZoneGeneratorComponent>(uid);
        zone.ImmuneRoles.Clear();
        zone.ImmunePlaytime = null;
    }

    private void OnShutdown(EntityUid uid, ApartmentProtectedGridComponent comp, ComponentShutdown args)
    {
        if (!_states.Remove(uid, out var state) || TerminatingOrDeleted(uid)) return;
        if (state.AddedZone) RemComp<PacifiedZoneGeneratorComponent>(uid);
        else if (TryComp<PacifiedZoneGeneratorComponent>(uid, out var zone))
        {
            zone.WholeGrid = state.WholeGrid;
            zone.DisallowAllCombat = state.Combat;
            zone.ImmuneRoles = state.ImmuneRoles;
            zone.ImmunePlaytime = state.ImmunePlaytime;
        }
        if (state.AddedProtection) RemComp<ProtectedGridComponent>(uid);
        else if (TryComp<ProtectedGridComponent>(uid, out var protection))
        {
            protection.PreventFloorRemoval = state.FloorRemoval;
            protection.PreventFloorPlacement = state.FloorPlacement;
            protection.PreventRCDUse = state.Rcd;
            protection.PreventEmpEvents = state.Emp;
            protection.PreventExplosions = state.Explosions;
            protection.PreventArtifactTriggers = state.Artifacts;
            protection.KillHostileMobs = state.Hostiles;
            Dirty(uid, protection);
        }
    }
}

using Content.Server.Atmos.EntitySystems;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Atmos;
using Robust.Server.GameObjects;
using Robust.Shared.Map.Components;

namespace Content.Server._DeepLagoon.Apartments;

[RegisterComponent]
public sealed partial class ApartmentAtmosphereSettingsComponent : Component
{
    [DataField] public int OxygenPercent = ApartmentAtmosphereComponent.DefaultOxygenPercent;
}

/// <summary>Every controller in an apartment edits the same grid-wide air settings.</summary>
public sealed partial class ApartmentAtmosphereSystem : EntitySystem
{
    [Dependency] private AtmosphereSystem _atmos = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private ApartmentSystem _apartments = default!;
    private HashSet<EntityUid> _regulated = new();
    private float _elapsed;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ApartmentGridComponent, SafeGridAtmosphereEvent>(OnSafeAtmosphere);
        SubscribeLocalEvent<ApartmentAtmosphereComponent, BoundUIOpenedEvent>(OnOpen);
        SubscribeLocalEvent<ApartmentAtmosphereComponent, ApartmentAtmosphereSetMessage>(OnSet);
    }

    private bool TryGetApartment(EntityUid uid, out EntityUid grid)
    {
        grid = default;
        if (TerminatingOrDeleted(uid) || !TryComp(uid, out TransformComponent? transform) || !transform.Anchored ||
            transform.GridUid is not { } parent || TerminatingOrDeleted(parent) || !HasComp<ApartmentGridComponent>(parent) || !HasComp<MapGridComponent>(parent))
            return false;
        grid = parent;
        return true;
    }

    private bool HasController(EntityUid grid)
    {
        var query = EntityQueryEnumerator<ApartmentAtmosphereComponent>();
        while (query.MoveNext(out var uid, out _))
            if (TryGetApartment(uid, out var parent) && parent == grid)
                return true;
        return false;
    }

    private void OnSafeAtmosphere(Entity<ApartmentGridComponent> entity, ref SafeGridAtmosphereEvent args)
    {
        if (!HasController(entity))
            return;
        var percent = TryComp<ApartmentAtmosphereSettingsComponent>(entity, out var settings)
            ? Math.Clamp(settings.OxygenPercent, 0, 100) : ApartmentAtmosphereComponent.DefaultOxygenPercent;
        var moles = Atmospherics.MolesCellStandard * args.Air.Volume / Atmospherics.CellVolume;
        args.Air.SetMoles(Gas.Oxygen, moles * percent / 100f);
        args.Air.SetMoles(Gas.Nitrogen, moles * (100 - percent) / 100f);
    }

    private void OnOpen(EntityUid uid, ApartmentAtmosphereComponent component, BoundUIOpenedEvent args) => Publish(uid);

    private void OnSet(EntityUid uid, ApartmentAtmosphereComponent component, ApartmentAtmosphereSetMessage args)
    {
        if (args.UiKey is not ApartmentAtmosphereUiKey.Key || args.OxygenPercent is < 0 or > 100 ||
            !TryGetApartment(uid, out var grid) || Transform(args.Actor).GridUid != grid)
            return;
        if (!_apartments.SaveAtmosphereSettings(grid, args.OxygenPercent))
        {
            Publish(uid, false);
            return;
        }
        EnsureComp<ApartmentAtmosphereSettingsComponent>(grid).OxygenPercent = args.OxygenPercent;
        _atmos.SetSafeGridAtmosphere(grid);
        _regulated.Add(grid);
        var query = EntityQueryEnumerator<ApartmentAtmosphereComponent>();
        while (query.MoveNext(out var device, out _))
            if (TryGetApartment(device, out var parent) && parent == grid)
                Publish(device);
    }

    private void Publish(EntityUid uid, bool saved = true)
    {
        var active = TryGetApartment(uid, out var grid);
        var percent = active && TryComp<ApartmentAtmosphereSettingsComponent>(grid, out var settings)
            ? settings.OxygenPercent : ApartmentAtmosphereComponent.DefaultOxygenPercent;
        _ui.SetUiState(uid, ApartmentAtmosphereUiKey.Key, new ApartmentAtmosphereState(percent, active, saved));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _elapsed += frameTime;
        if (_elapsed < 1)
            return;
        _elapsed = 0;
        var current = new HashSet<EntityUid>();
        var query = EntityQueryEnumerator<ApartmentAtmosphereComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (TryGetApartment(uid, out var grid) && current.Add(grid))
                _atmos.SetSafeGridAtmosphere(grid);
            Publish(uid);
        }
        // Removing or moving the last controller restores the apartment's standard safe air.
        foreach (var grid in _regulated)
            if (!current.Contains(grid) && !TerminatingOrDeleted(grid) && HasComp<ApartmentGridComponent>(grid))
                _atmos.SetSafeGridAtmosphere(grid);
        _regulated = current;
    }
}

using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Mapping;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.Atmos;
using Content.Shared.Interaction.Events;
using Content.Shared.Mining.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunShelterCapsuleComponent : Component
{
    public string? LastFailure;
    [DataField(required: true)] public ResPath Template;
    [DataField] public int Width = 5;
    [DataField] public int Height = 5;
    public TimeSpan DeployAt;
    public EntityUid? User;
    public bool Armed;
}

[RegisterComponent]
public sealed partial class BitrunShelterFridgeComponent : Component;

[RegisterComponent]
public sealed partial class BitrunCapsulePlacementPolicyComponent : Component
{
    [DataField] public HashSet<Vector2i> BlockedTiles = new();
}

/// <summary>Deploys the authored WhiteMoon shelter into the existing grid, not an overlapping second grid.</summary>
public sealed class BitrunShelterSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private AtmosphereSystem _atmos = default!;
    [Dependency] private GridEnvironmentSystem _environment = default!;
    [Dependency] private PowerReceiverSystem _power = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedStunSystem _stun = default!;
    [Dependency] private ThrowingSystem _throwing = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunShelterCapsuleComponent, UseInHandEvent>((Entity<BitrunShelterCapsuleComponent> ent, ref UseInHandEvent args) =>
        {
            if (args.Handled || ent.Comp.Armed) return;
            args.Handled = true;
            ent.Comp.Armed = true;
            ent.Comp.User = args.User;
            ent.Comp.DeployAt = _timing.CurTime + TimeSpan.FromSeconds(5);
            _popup.PopupEntity(Loc.GetString("bitrun-shelter-arming"), ent, args.User);
        });
        SubscribeLocalEvent<BitrunShelterFridgeComponent, ContainerIsInsertingAttemptEvent>((Entity<BitrunShelterFridgeComponent> ent, ref ContainerIsInsertingAttemptEvent args) =>
        {
            if (args.Container.ID == "smart_fridge_inventory" && args.Container.ContainedEntities.Count >= 10) args.Cancel();
        });
    }

    private bool Fail(Entity<BitrunShelterCapsuleComponent> capsule, string reason)
    {
        capsule.Comp.LastFailure = reason;
        capsule.Comp.Armed = false;
        if (capsule.Comp.User is { } user && !Deleted(user))
            _popup.PopupEntity(Loc.GetString("bitrun-shelter-blocked", ("width", capsule.Comp.Width), ("height", capsule.Comp.Height)), capsule, user);
        return false;
    }

    public bool TryExpand(Entity<BitrunShelterCapsuleComponent> capsule)
    {
        var transform = Transform(capsule);
        if (transform.GridUid is not { } grid || transform.MapUid is not { } map ||
            !TryComp<MapGridComponent>(grid, out var gridComp)) return Fail(capsule, "no-ground");
        var location = _transform.ToCoordinates(grid, _transform.GetMapCoordinates(capsule));
        var center = _maps.CoordinatesToTile(grid, gridComp, location);
        var minimum = center - new Vector2i(capsule.Comp.Width / 2, capsule.Comp.Height / 2);
        var rocks = new HashSet<EntityUid>();
        for (var x = 0; x < capsule.Comp.Width; x++)
        for (var y = 0; y < capsule.Comp.Height; y++)
        {
            var tile = minimum + new Vector2i(x, y);
            // SS14's ShuttleComponent is also added to ordinary virtual grids;
            // preserve the actual source /area restrictions instead.
            if (TryComp<BitrunCapsulePlacementPolicyComponent>(grid, out var policy) && policy.BlockedTiles.Contains(tile)) return Fail(capsule, "restricted-area");
            if (_maps.GetTileRef(grid, gridComp, tile).Tile.IsEmpty) return Fail(capsule, "outside-ground:" + tile);
            foreach (var uid in _maps.GetAnchoredEntities(grid, gridComp, tile))
            {
                if (HasComp<MiningScannerViewableComponent>(uid)) { rocks.Add(uid); continue; }
                if (MetaData(uid).EntityPrototype?.ID.Contains("StructureStoneTile", StringComparison.Ordinal) == true ||
                    TryComp<PhysicsComponent>(uid, out var physics) && physics.CanCollide &&
                    TryComp<FixturesComponent>(uid, out var fixtures) && fixtures.Fixtures.Values.Any(fixture => fixture.Hard))
                    return Fail(capsule, "blocked:" + MetaData(uid).EntityPrototype?.ID + ":" + tile);
            }
        }
        if (!_loader.TryLoadGridTracked(_transform.GetMapCoordinates(capsule).MapId, capsule.Comp.Template, out var loaded) || loaded == null) return Fail(capsule, "template-load");
        var source = loaded.Value;
        try
        {
            RemComp<Content.Shared._DeepLagoon.Mapping.GridEnvironmentComponent>(source);
            foreach (var rock in rocks) QueueDel(rock);
            var origin = _transform.GetMapCoordinates(capsule).Position;
            foreach (var victim in _lookup.GetEntitiesInRange<MobStateComponent>(location, Math.Max(capsule.Comp.Width, capsule.Comp.Height)))
            {
                var delta = _transform.GetWorldPosition(victim) - origin;
                if (Math.Abs(delta.X) > capsule.Comp.Width / 2f || Math.Abs(delta.Y) > capsule.Comp.Height / 2f) continue;
                _stun.TryParalyze(victim, TimeSpan.FromSeconds(3), true);
                _stun.TryKnockdown(victim, TimeSpan.FromSeconds(6), true);
                if (delta.LengthSquared() < 0.01f) delta = Vector2.UnitY;
                _throwing.TryThrow(victim, Vector2.Normalize(delta) * Math.Max(capsule.Comp.Width, capsule.Comp.Height), 3, recoil: false, doSpin: false);
            }
            var tiles = _maps.GetAllTiles(source, source.Comp).Where(tile => !tile.Tile.IsEmpty)
                .Select(tile => (tile.GridIndices + minimum, tile.Tile)).ToList();
            _maps.SetTiles(grid, gridComp, tiles);
            var children = Transform(source).ChildEnumerator;
            var moved = new List<EntityUid>();
            while (children.MoveNext(out var uid)) moved.Add(uid);
            foreach (var uid in moved)
            {
                var xform = Transform(uid);
                var destination = new EntityCoordinates(grid, xform.LocalPosition + (Vector2)minimum);
                var anchored = xform.Anchored;
                if (anchored) _transform.Unanchor(uid, xform);
                _transform.SetCoordinates(uid, destination);
                if (anchored) _transform.AnchorEntity((uid, xform, MetaData(uid)), destination);
            }
            _environment.RefreshPower();
            foreach (var uid in moved)
                if (TryComp<ApcPowerReceiverComponent>(uid, out var receiver)) _power.SetNeedsPower(uid, false, receiver);
            var air = new GasMixture(Atmospherics.CellVolume) { Temperature = Atmospherics.T20C };
            air.SetMoles(Gas.Oxygen, Atmospherics.OxygenMolesStandard);
            air.SetMoles(Gas.Nitrogen, Atmospherics.NitrogenMolesStandard);
            _atmos.SetInitialTileAtmosphere(grid, tiles.Select(tile => tile.Item1), air);
            QueueDel(capsule);
            return true;
        }
        finally { if (!Deleted(source)) Del(source); }
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<BitrunShelterCapsuleComponent>();
        while (query.MoveNext(out var uid, out var capsule))
            if (capsule.Armed && capsule.DeployAt <= _timing.CurTime) TryExpand((uid, capsule));
    }
}

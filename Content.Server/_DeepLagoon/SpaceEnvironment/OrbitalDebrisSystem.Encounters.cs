using System.Numerics;
using System.Linq;
using Content.Server._DeepLagoon.Mapping;
using Content.Server._Mono.GridClaimer;
using Content.Server.Shuttles.Components;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Audio.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Utility;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

[RegisterComponent]
public sealed partial class SpaceBlueWreckComponent : Component;

[RegisterComponent]
public sealed partial class SpaceDerelictComponent : Component
{
    public string Region = "";
    public int Slot;
}

public sealed partial class OrbitalDebrisSystem
{
    private sealed class DerelictField
    {
        public int Used;
        public TimeSpan Next;
    }

    private readonly Dictionary<(EntityUid Map, string Region), DerelictField> _derelictFields = new();
    private TimeSpan _nextDerelictLoad;
    // Compact existing Derelict assets from BluespaceSalvage; no new ship layouts.
    public static readonly string[] DerelictMaps = [
        "/Maps/_NF/Shuttles/Scrap/disciple.yml", "/Maps/_NF/Shuttles/Scrap/orange.yml",
        "/Maps/_NF/Shuttles/Scrap/point.yml", "/Maps/_NF/Shuttles/Scrap/nugget.yml",
        "/Maps/_NF/Shuttles/Scrap/canister.yml",
    ];
    public static int DerelictLimit(uint seed, string region) => 1 + (int) (SectorRandom.Derive(seed, $"derelict/{region}") % 3);

    private bool TryFindDerelict(EntityUid map, Vector2 position, out string regionId, out int slot)
    {
        regionId = "";
        slot = -1;
        if (_timing.CurTime < _nextDerelictLoad || !TryComp<SpaceEnvironmentComponent>(map, out var environment)
            || string.IsNullOrEmpty(environment.Campaign)) return false;
        foreach (var region in environment.Definition.Regions)
        {
            if (region.Kind != SpaceRegionKind.Asteroids || !SpaceRegionGeometry.Contains(region, position)) continue;
            var key = (map, region.Id);
            var store = EntityManager.System<SectorSystem>().Store;
            var limit = DerelictLimit(environment.Definition.Seed, region.Id);
            if (!_derelictFields.TryGetValue(key, out var field))
            {
                field = new DerelictField { Used = store.DerelictSlots(environment.Campaign, environment.Definition.Id, region.Id) };
                _derelictFields.Add(key, field);
            }
            if (_timing.CurTime < field.Next) return false;
            for (var candidate = 0; candidate < limit; candidate++)
            {
                var bit = 1 << candidate;
                if ((field.Used & bit) != 0) continue;
                regionId = region.Id;
                slot = candidate;
                return true;
            }
            return false;
        }
        return false;
    }

    private EntityUid? SpawnDerelict(EntityUid map, Vector2 position, Vector2 anchor, uint seed, string region, int slot)
    {
        // Mapping is heavier than a scrap pile: at most one load attempt per five seconds globally.
        _nextDerelictLoad = _timing.CurTime + TimeSpan.FromSeconds(5);
        var path = new ResPath(DerelictMaps[seed % (uint) DerelictMaps.Length]);
        if (!EntityManager.System<MapLoaderSystem>().TryLoadGridTracked(Comp<MapComponent>(map).MapId, path,
                out var loaded, offset: position)) return null;
        var uid = loaded.Value.Owner;
        var grid = loaded.Value.Comp;
        var center = grid.LocalAABB.Center;
        var extent = grid.LocalAABB.Size.Length() * 0.5f;
        var (_, rotation) = _transform.GetWorldPositionRotation(uid);
        _transform.SetWorldPosition(uid, position - rotation.RotateVec(center));
        var environment = Comp<SpaceEnvironmentComponent>(map);
        var random = new SectorRandom(seed);
        var state = new Pile { Map = map, Anchor = anchor, Radius = MathF.Max(1, Vector2.Distance(position, anchor)),
            Speed = random.Range(2f, 6f), Direction = random.Index(2) == 0 ? -1 : 1,
            Center = center, HalfExtent = MathF.Max(8, extent), LastSeen = _timing.CurTime };
        var tiles = _maps.GetAllTiles(uid, grid);
        while (tiles.MoveNext()) state.Tiles.Add(tiles.Current.GridIndices, tiles.Current.Tile);
        var pending = new Stack<EntityUid>();
        var children = Transform(uid).ChildEnumerator;
        while (children.MoveNext(out var child)) { state.Children.Add(child); pending.Push(child); }
        var descendants = new List<EntityUid>();
        while (pending.TryPop(out var child) && descendants.Count <= 768)
        {
            descendants.Add(child);
            var nested = Transform(child).ChildEnumerator;
            while (nested.MoveNext(out var nestedChild)) pending.Push(nestedChild);
        }
        if (!HasComp<ShuttleComponent>(uid) || state.Tiles.Count > 256 || extent > 32 || descendants.Count > 768
            || !CanAddAt(map, position, state.HalfExtent)
            || _observers.Exists(view => Visible(view, map, position, state.HalfExtent - 28f))
            || !EntityManager.System<SectorSystem>().Store.TryRecordDerelict(environment.Campaign, environment.Definition.Id, region, slot))
        {
            QueueDel(uid);
            return null;
        }
        var field = _derelictFields[(map, region)];
        field.Used |= 1 << slot;
        field.Next = _timing.CurTime + TimeSpan.FromSeconds(90);
        var marker = AddComp<SpaceDerelictComponent>(uid);
        marker.Region = region;
        marker.Slot = slot;
        EnsureComp<ClaimableGridComponent>(uid);
        foreach (var descendant in descendants)
            if (!Transform(descendant).Anchored && !HasComp<AudioComponent>(descendant)) _looseLoot[descendant] = uid;
        var body = EnsureComp<PhysicsComponent>(uid);
        _physics.SetBodyType(uid, BodyType.Dynamic, body: body);
        var radial = Normal(position - anchor);
        _physics.SetLinearVelocity(uid, new Vector2(-radial.Y, radial.X) * state.Speed * state.Direction, body: body);
        _physics.SetAngularVelocity(uid, 0, body: body);
        SetEncounterIdentity(uid, false, true, seed);
        _piles.Add(uid, state);
        return uid;
    }

    private static string WreckWall(ref SectorRandom random) => random.Index(4) switch
    {
        0 => "WallSolidRust", 1 => "Girder", 2 => "GrilleBroken", _ => "Window",
    };

    private void SetEncounterIdentity(EntityUid grid, bool blue, bool derelict, uint seed)
    {
        if (blue) AddComp<SpaceBlueWreckComponent>(grid);
        if (blue || derelict)
        {
            var shuttles = EntityManager.System<SharedShuttleSystem>();
            shuttles.SetIFFColor(grid, Color.FromHex(derelict ? "#AAAAAA" : "#88b0d1")); // Native salvage colours.
            shuttles.AddIFFFlag(grid, IFFFlags.AlwaysShowColor | (derelict ? IFFFlags.None : IFFFlags.HideLabel | IFFFlags.HideLabelAlways));
        }
        EntityManager.System<MetaDataSystem>().SetEntityName(grid, derelict ? $"Derelict-{seed % 100000:00000}"
            : Loc.GetString(blue ? "space-environment-blue-wreck" : "space-environment-space-junk"));
    }

    private void ForgetDerelictFields(EntityUid map)
    {
        // At most 160 descriptors on each of the bounded loaded maps; no history-sized cache.
        foreach (var key in _derelictFields.Keys.Where(key => key.Map == map).ToArray()) _derelictFields.Remove(key);
    }

    private Vector2 PilePosition(EntityUid uid, Pile pile)
    {
        var (position, rotation) = _transform.GetWorldPositionRotation(uid);
        return position + rotation.RotateVec(pile.Center);
    }
}

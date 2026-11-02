using System.Numerics;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

[RegisterComponent]
public sealed partial class SpaceChromiteAsteroidComponent : Component;

[RegisterComponent]
public sealed partial class NavigationSignalComponent : Component
{
    public string Site = "";
}

public sealed partial class OrbitalDebrisSystem
{
    public bool SiteAllowed(EntityUid map, Vector2 position) => PositionClear(map, position);

    private bool TrySpawnSite(Viewer viewer, Observer observer, SpaceEnvironmentComponent environment)
    {
        if (observer.Velocity.LengthSquared() > 500f * 500f) return false;
        var navigation = EntityManager.System<SpaceNavigationSystem>();
        var sites = navigation.Sites(observer.Map, environment);
        foreach (var site in sites.Sites)
        {
            // Boarding releases ambient ownership but must not duplicate a still-existing relay.
            if (sites.LiveGrids.TryGetValue(site.Id, out var live) && !TerminatingOrDeleted(live)) continue;
            if (sites.Completed.Contains(site.Id) || !NavigationSiteCatalog.Available(environment.Definition, site)
                || Vector2.DistanceSquared(site.Position, observer.Position) > 1200f * 1200f
                || _observers.Exists(view => Visible(view, observer.Map, site.Position, 12f))
                || !CanAddAt(observer.Map, site.Position) || !PositionClear(observer.Map, site.Position) || NearGrid(observer.Map, site.Position)) continue;
            if (observer.Velocity.LengthSquared() > 16f)
            {
                var speed = observer.Velocity.Length();
                if (Vector2.Dot(site.Position - observer.Position, observer.Velocity / speed)
                    < observer.Range + (speed + 8f) * ReactionSeconds + 16f) continue;
            }
            var exists = false;
            foreach (var pile in _piles.Values)
                if (pile.Map == observer.Map && pile.SiteId == site.Id) { exists = true; break; }
            if (exists) continue;
            var grid = _maps.CreateGridEntity(Comp<Robust.Shared.Map.Components.MapComponent>(observer.Map).MapId);
            _transform.SetWorldPosition(grid, site.Position);
            var state = new Pile { Map = observer.Map, Anchor = site.Position, SiteId = site.Id,
                LastSeen = _timing.CurTime, Sponsor = viewer.Session,
                InterceptUntil = _timing.CurTime + TimeSpan.FromSeconds(90) };
            var floor = new Tile(_tiles[SupportTile].TileId);
            var cells = new List<(Vector2i, Tile)>();
            for (var x = 0; x < 2; x++)
            for (var y = 0; y < 3; y++)
            {
                var index = new Vector2i(x, y);
                cells.Add((index, floor));
                state.Tiles.Add(index, floor);
            }
            _maps.SetTiles(grid, cells);
            var marker = Spawn("DeepLagoonNavigationRelay", new EntityCoordinates(grid, new Vector2(0.5f, 1.5f)));
            EnsureComp<NavigationSignalComponent>(marker).Site = site.Id;
            EntityManager.System<MetaDataSystem>().SetEntityName(marker, navigation.SiteName(site));
            var children = Transform(grid).ChildEnumerator;
            while (children.MoveNext(out var child)) state.Children.Add(child);
            var body = EnsureComp<PhysicsComponent>(grid);
            _physics.SetBodyType(grid, BodyType.Dynamic, body: body);
            var shuttles = EntityManager.System<SharedShuttleSystem>();
            shuttles.SetIFFColor(grid, Color.FromHex("#67d8cc"));
            shuttles.AddIFFFlag(grid, IFFFlags.AlwaysShowColor);
            EntityManager.System<MetaDataSystem>().SetEntityName(grid, navigation.SiteName(site));
            _piles.Add(grid, state);
            sites.LiveGrids[site.Id] = grid;
            return true;
        }
        return false;
    }

    private static string ChromiteWall(ref SectorRandom random)
    {
        var roll = random.Index(100);
        return roll < 55 ? "WallRockChromite" : roll < 70 ? "WallRockChromiteTin"
            : roll < 80 ? "WallRockChromiteQuartz" : roll < 93 ? "WallRockChromitePlasma"
            : roll < 98 ? "WallRockChromiteGold" : "WallRockChromiteSilver";
    }
}

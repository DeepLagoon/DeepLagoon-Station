namespace Content.Server.NPC.Pathfinding;

public sealed partial class PathfindingSystem
{
    public void CollectMemoryDiagnostics(Action<string, long> add)
    {
        add("navigation.requests", _pathRequests.Count);
        add("navigation.request_capacity", _pathRequests.Capacity);
        add("navigation.portals", _portals.Count);
        add("navigation.dirty_grids", _dirtyNavGrids.Count);
        add("navigation.queued_grids", _queuedNavGrids.Count);
        add("navigation.debug_subscribers", _subscribedSessions.Count);
    }
}

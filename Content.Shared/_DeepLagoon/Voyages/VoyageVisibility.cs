namespace Content.Shared._DeepLagoon.Voyages;

/// <summary>Exploration reveals the next connected frontier, rather than the entire galaxy.</summary>
public static class VoyageVisibility
{
    public static HashSet<string> VisibleSystems(VoyageRouteModel graph, string currentSector = "")
    {
        var explored = new HashSet<string>();
        foreach (var system in graph.Systems)
            if (system.Known || system.Id == currentSector) explored.Add(system.Id);
        var visible = new HashSet<string>(explored);
        foreach (var lane in graph.Lanes)
        {
            if (explored.Contains(lane.From)) visible.Add(lane.To);
            if (explored.Contains(lane.To)) visible.Add(lane.From);
        }
        return visible;
    }
}

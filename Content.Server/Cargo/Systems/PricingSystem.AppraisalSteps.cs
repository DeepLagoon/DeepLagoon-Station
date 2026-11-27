using Robust.Shared.Containers;

namespace Content.Server.Cargo.Systems;

public sealed partial class PricingSystem
{
    /// <summary>
    /// Cooperative equivalent of AppraiseGrid for a private, immutable grid. Each step prices at most
    /// one entity. Container subtotals keep the same floating-point addition order as GetPriceConditional.
    /// Disposing the iterator abandons appraisal without publishing a partial result.
    /// </summary>
    public IEnumerable<bool> AppraiseGridSteps(EntityUid grid, Action<double> completed,
        Func<EntityUid, bool>? predicate = null)
    {
        var visited = new HashSet<EntityUid>();
        var frames = new Stack<AppraisalFrame>();
        using var roots = AppraisalRoots(grid).GetEnumerator();
        var total = 0.0;
        try
        {
            while (true)
            {
                if (frames.TryPeek(out var frame))
                {
                    if (frame.Children.MoveNext())
                    {
                        var child = frame.Children.Current;
                        if (visited.Add(child) && (predicate == null || predicate(child)))
                            frames.Push(new AppraisalFrame(GetPriceConditional(child, false), AppraisalContents(child).GetEnumerator()));
                    }
                    else
                    {
                        frames.Pop();
                        frame.Children.Dispose();
                        if (frames.TryPeek(out var parent)) parent.Total += frame.Total;
                        else total += frame.Total;
                    }
                }
                else if (roots.MoveNext())
                {
                    var child = roots.Current;
                    if (visited.Add(child) && (predicate == null || predicate(child)))
                        frames.Push(new AppraisalFrame(GetPriceConditional(child, false), AppraisalContents(child).GetEnumerator()));
                }
                else break;
                yield return true;
            }
            completed(total);
        }
        finally
        {
            while (frames.TryPop(out var frame)) frame.Children.Dispose();
        }
    }

    private IEnumerable<EntityUid> AppraisalRoots(EntityUid grid)
    {
        var children = Transform(grid).ChildEnumerator;
        while (children.MoveNext(out var child)) yield return child;
    }

    private IEnumerable<EntityUid> AppraisalContents(EntityUid uid)
    {
        if (!TryComp<ContainerManagerComponent>(uid, out var manager)) yield break;
        foreach (var container in manager.Containers.Values)
            foreach (var child in container.ContainedEntities)
                yield return child;
    }

    private sealed class AppraisalFrame(double total, IEnumerator<EntityUid> children)
    {
        public double Total = total;
        public readonly IEnumerator<EntityUid> Children = children;
    }
}

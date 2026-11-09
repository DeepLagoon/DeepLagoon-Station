using System.Diagnostics;
using Content.Shared.Materials;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server.Materials;

public sealed partial class MaterialStorageSystem
{
    [Dependency] private SharedTransformSystem _spillTransform = default!;
    [Dependency] private SharedMapSystem _spillMap = default!;

    private const int SpillStacksPerTick = 64;
    private const int SpillStacksPerStorage = 16;
    private const double SpillMillisecondsPerTick = 2;

    private readonly Queue<MaterialSpill> _spills = new();

    private readonly record struct SpillStack(EntProtoId Prototype, int Count, int StackLimit);

    private sealed class MaterialSpill(EntityCoordinates coordinates, MapCoordinates lastPosition, Queue<SpillStack> stacks)
    {
        public EntityCoordinates Coordinates = coordinates;
        public MapCoordinates LastPosition = lastPosition;
        public readonly Queue<SpillStack> Stacks = stacks;
        public int SpawnedFromCurrentStack;
    }

    private void SpillOnDestruction(Entity<MaterialStorageComponent> storage)
    {
        if (storage.Comp.Storage.Count == 0)
            return;

        var stacks = new Queue<SpillStack>();
        foreach (var (id, amount) in storage.Comp.Storage)
        {
            if (amount <= 0)
                continue;

            if (!_prototypeManager.TryIndex(id, out var material))
            {
                Log.Error($"Failed to index material prototype {id}");
                continue;
            }

            if (material.StackEntity is not { } prototype || !TryGetMaterialPerSheet(material, out var perSheet))
                continue;

            var sheets = amount / perSheet;
            if (sheets == 0)
                continue;

            var stackLimit = _stackSystem.GetMaxCount(prototype);
            if (stackLimit <= 0)
                continue;

            stacks.Enqueue(new SpillStack(prototype, sheets, stackLimit));
        }

        // The destroyed entity will be deleted this tick. Move ownership of the contents to the spill.
        storage.Comp.Storage.Clear();
        if (stacks.Count == 0)
            return;

        var coordinates = Transform(storage).Coordinates;
        var spill = new MaterialSpill(coordinates, _spillTransform.ToMapCoordinates(coordinates), stacks);
        _spills.Enqueue(spill);
    }

    public override void Update(float frameTime)
    {
        if (_spills.Count == 0)
            return;

        var start = Stopwatch.GetTimestamp();
        var budget = SpillStacksPerTick;
        while (budget > 0 && _spills.TryDequeue(out var spill))
        {
            // Follow the ship while it exists. If the grid was destroyed, spill into the surrounding map.
            if (!TerminatingOrDeleted(spill.Coordinates.EntityId))
                spill.LastPosition = _spillTransform.ToMapCoordinates(spill.Coordinates);
            else if (_spillMap.MapExists(spill.LastPosition.MapId))
                spill.Coordinates = _spillTransform.ToCoordinates(spill.LastPosition);
            else
                continue;

            for (var i = 0; i < SpillStacksPerStorage && spill.Stacks.TryPeek(out var stack); i++)
            {
                var amount = Math.Min(stack.Count - spill.SpawnedFromCurrentStack, stack.StackLimit);
                _stackSystem.SpawnStackAtPosition(stack.Prototype, amount, spill.Coordinates);
                spill.SpawnedFromCurrentStack += amount;
                if (spill.SpawnedFromCurrentStack == stack.Count)
                {
                    spill.Stacks.Dequeue();
                    spill.SpawnedFromCurrentStack = 0;
                }

                if (--budget == 0 || Stopwatch.GetElapsedTime(start).TotalMilliseconds >= SpillMillisecondsPerTick)
                    break;
            }

            if (spill.Stacks.Count != 0)
                _spills.Enqueue(spill);

            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= SpillMillisecondsPerTick)
                break;
        }
    }
}

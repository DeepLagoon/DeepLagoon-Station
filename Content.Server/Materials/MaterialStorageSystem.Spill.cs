using System.Diagnostics;
using Content.Server.Storage.Components;
using Content.Shared.Materials;
using Content.Shared.Tag;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server.Materials;

public sealed partial class MaterialStorageSystem
{
    [Dependency] private SharedTransformSystem _spillTransform = default!;
    [Dependency] private SharedMapSystem _spillMap = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private MetaDataSystem _metadata = default!;

    private const int SpillStacksPerTick = 64;
    private const int SpillStacksPerStorage = 16;
    private const double SpillMillisecondsPerTick = 2;

    private readonly Queue<MaterialSpill> _spills = new();

    private static readonly EntProtoId ResiduePrototype = "MaterialResidue";
    private static readonly ProtoId<TagPrototype>[] MaterialTags = ["Ore", "Raw", "Wooden", "Sheet", "Metal", "Plastic", "Ingot"];

    private readonly record struct SpillStack(EntProtoId Prototype, int Count, int StackLimit, MaterialPrototype? Residue = null);

    private sealed class MaterialSpill(EntityCoordinates coordinates, MapCoordinates lastPosition, Queue<SpillStack> stacks)
    {
        public EntityCoordinates Coordinates = coordinates;
        public MapCoordinates LastPosition = lastPosition;
        public readonly Queue<SpillStack> Stacks = stacks;
        public int SpawnedFromCurrentStack;
    }

    private void QueueMaterialSpill(Entity<MaterialStorageComponent> storage)
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

            var remainder = amount;
            if (material.StackEntity is { } prototype && TryGetMaterialPerSheet(material, out var perSheet))
            {
                var sheets = amount / perSheet;
                var limit = _stackSystem.GetMaxCount(prototype);
                if (sheets > 0 && limit > 0)
                {
                    stacks.Enqueue(new SpillStack(prototype, sheets, limit));
                    remainder %= perSheet;
                }
            }

            if (remainder > 0)
                stacks.Enqueue(new SpillStack(ResiduePrototype, remainder, remainder, material));
        }

        // Transfer the local stock to the spill and keep the old machine from collecting it this tick.
        if (TryComp<MaterialStorageMagnetPickupComponent>(storage, out var magnet))
            magnet.MagnetEnabled = false;
        storage.Comp.Storage.Clear();
        Dirty(storage);
        if (stacks.Count == 0)
            return;

        var coordinates = Transform(storage).Coordinates;
        var spill = new MaterialSpill(coordinates, _spillTransform.ToMapCoordinates(coordinates), stacks);
        _spills.Enqueue(spill);
    }

    private void SpawnResidue(SpillStack drop, MaterialPrototype material, EntityCoordinates coordinates)
    {
        var residue = SpawnAtPosition(drop.Prototype, coordinates);
        AddComp<PhysicalCompositionComponent>(residue).MaterialComposition.Add(material.ID, drop.Count);

        if (material.StackEntity is { } stack && _prototypeManager.TryIndex(stack, out var prototype) &&
            prototype.TryComp<TagComponent>(out var tags, EntityManager.ComponentFactory))
        {
            foreach (var tag in MaterialTags)
            {
                if (_tags.HasAllTags(tags, tag))
                    _tags.AddTags(residue, tag);
            }
        }

        _metadata.SetEntityName(residue, Loc.GetString("material-residue-name", ("material", Loc.GetString(material.Name))));
        _metadata.SetEntityDescription(residue, Loc.GetString("material-residue-description", ("amount", drop.Count)));
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
                if (stack.Residue is { } material)
                    SpawnResidue(stack, material, spill.Coordinates);
                else
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

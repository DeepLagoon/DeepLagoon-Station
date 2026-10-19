using System.Collections.Generic;
using System.Linq;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Atmos;
using NUnit.Framework;
using Robust.Shared.Maths;

namespace Content.Tests.Shared;

[TestFixture]
public sealed class GridEditorAtmosTests
{
    private static Dictionary<Vector2i, GridEditorAtmosCell> Room()
    {
        var cells = new Dictionary<Vector2i, GridEditorAtmosCell>();
        for (var x = 0; x < 3; x++) for (var y = 0; y < 3; y++)
            cells[new(x, y)] = new GridEditorAtmosCell { X = x, Y = y,
                Blocked = x == 1 && y == 1 ? AtmosDirection.Invalid : AtmosDirection.All, NoAirWhenBlocked = true };
        return cells;
    }

    [Test]
    public void SealedRoomHasNoLeakAndWallVolumesAreExcluded()
    {
        var cells = Room(); GridEditorAtmosTopology.Analyze(cells);
        Assert.That(cells[new(1, 1)].Status, Is.EqualTo(GridEditorAirStatus.Sealed));
        Assert.That(cells.Values.Count(c => c.Status == GridEditorAirStatus.Blocked), Is.EqualTo(8));
        Assert.That(cells.Values.All(c => c.Leaks == AtmosDirection.Invalid), Is.True);
    }

    [Test]
    public void OpenDoorAndMissingFloorConnectTheWholeRoomToSpace()
    {
        var cells = Room(); cells[new(2, 1)].Blocked = AtmosDirection.Invalid;
        GridEditorAtmosTopology.Analyze(cells);
        Assert.That(cells[new(1, 1)].Status, Is.EqualTo(GridEditorAirStatus.Leaking));
        Assert.That(cells[new(2, 1)].Leaks, Is.EqualTo(AtmosDirection.East));
        cells.Remove(new(2, 1)); GridEditorAtmosTopology.Analyze(cells);
        Assert.That(cells[new(1, 1)].Leaks, Is.EqualTo(AtmosDirection.East));
    }

    [Test]
    public void DirectionalWallBlocksFromEitherSideAndRotatingItChangesLeakPath()
    {
        var cells = Room(); var diagonal = cells[new(2, 1)];
        diagonal.NoAirWhenBlocked = false; diagonal.Blocked = AtmosDirection.West;
        GridEditorAtmosTopology.Analyze(cells);
        Assert.That(cells[new(1, 1)].Status, Is.EqualTo(GridEditorAirStatus.Sealed));
        Assert.That(diagonal.Status, Is.EqualTo(GridEditorAirStatus.Leaking));
        diagonal.Blocked = AtmosDirection.North;
        GridEditorAtmosTopology.Analyze(cells);
        Assert.That(cells[new(1, 1)].Status, Is.EqualTo(GridEditorAirStatus.Leaking));
        // The source's own east blocker also closes this edge.
        cells[new(1, 1)].Blocked = AtmosDirection.East;
        GridEditorAtmosTopology.Analyze(cells);
        Assert.That(cells[new(1, 1)].Status, Is.EqualTo(GridEditorAirStatus.Sealed));
    }

    [Test]
    public void SpaceFloorIsAnAtmosphericHoleEvenInsideAnEnclosedShape()
    {
        var cells = Room(); cells[new(2, 1)].Blocked = AtmosDirection.Invalid; cells[new(2, 1)].MapAtmosphere = true;
        GridEditorAtmosTopology.Analyze(cells);
        Assert.That(cells[new(1, 1)].Status, Is.EqualTo(GridEditorAirStatus.Leaking));
        Assert.That(cells[new(2, 1)].Status, Is.EqualTo(GridEditorAirStatus.Space));
        Assert.That(cells[new(1, 1)].Leaks, Is.EqualTo(AtmosDirection.East));
    }
}

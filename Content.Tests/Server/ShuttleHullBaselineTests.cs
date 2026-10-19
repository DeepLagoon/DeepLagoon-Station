using System.Collections.Generic;
using System.Linq;
using Content.Server.Shuttles.Systems;
using NUnit.Framework;
using Robust.Shared.Maths;

namespace Content.Tests.Server;

[TestFixture]
public sealed class ShuttleHullBaselineTests
{
    private static Dictionary<(Vector2i Tile, byte Kind), int> Snapshot(params (int X, int Y, byte Kind, int Count)[] entries)
    {
        return entries.ToDictionary(e => (new Vector2i(e.X, e.Y), e.Kind), e => e.Count);
    }

    [Test]
    public void MissingDevicesKeepTheirOriginalFootprintAndReplacementRepairsThem()
    {
        var baseline = new ShuttleHullBaseline();
        var original = Snapshot((0, 0, ShuttleHullBaseline.Floor, 1), (3, 0, ShuttleHullBaseline.Thruster, 1));
        var healthy = baseline.Build(original);
        var lost = baseline.Build(Snapshot((0, 0, ShuttleHullBaseline.Floor, 1)));
        Assert.Multiple(() =>
        {
            Assert.That(lost.Width, Is.EqualTo(healthy.Width));
            Assert.That(lost.ExpectedThrusters, Is.EqualTo(1));
            Assert.That(lost.MissingThrusters, Is.EqualTo(1));
            Assert.That(lost.Cells[3] & 2, Is.EqualTo(2));
            Assert.That(lost.Cells[3] & ShuttleHullBaseline.Thruster, Is.EqualTo(ShuttleHullBaseline.Thruster));
        });

        var repaired = baseline.Build(original);
        Assert.That(repaired.MissingThrusters, Is.Zero);
        Assert.That(repaired.Cells[3] & 2, Is.Zero);
    }

    [Test]
    public void NewConstructionExpandsBaselineWithoutForgettingEarlierLoss()
    {
        var baseline = new ShuttleHullBaseline();
        baseline.Build(Snapshot((0, 0, ShuttleHullBaseline.Weapon, 1)));
        var expanded = baseline.Build(Snapshot((1, 0, ShuttleHullBaseline.Weapon, 1), (2, 0, ShuttleHullBaseline.Structure, 1)));
        Assert.Multiple(() =>
        {
            Assert.That(expanded.ExpectedWeapons, Is.EqualTo(2));
            Assert.That(expanded.MissingWeapons, Is.EqualTo(1));
            Assert.That(expanded.ExpectedStructures, Is.EqualTo(1));
        });
        var removed = baseline.Build(Snapshot());
        Assert.That(removed.MissingWeapons, Is.EqualTo(2));
        Assert.That(removed.MissingStructures, Is.EqualTo(1));
    }

    [Test]
    public void FastConstructionRemovalCanBeRecordedBeforePeriodicSnapshot()
    {
        var baseline = new ShuttleHullBaseline();
        baseline.Record(new Vector2i(4, 7), ShuttleHullBaseline.Structure);
        var state = baseline.Build(Snapshot());
        Assert.That(state.MissingStructures, Is.EqualTo(1));
        Assert.That(state.Cells[0] & 2, Is.EqualTo(2));
    }

    [Test]
    public void DifferentCategoryAtSameTileDoesNotRepairMissingWeapon()
    {
        var baseline = new ShuttleHullBaseline();
        baseline.Build(Snapshot((0, 0, ShuttleHullBaseline.Weapon, 1)));
        var state = baseline.Build(Snapshot((0, 0, ShuttleHullBaseline.Structure, 1)));
        Assert.That(state.MissingWeapons, Is.EqualTo(1));
        Assert.That(state.MissingStructures, Is.Zero);
    }

    [Test]
    public void MultipleStructuresAtSameTileRetainExpectedCount()
    {
        var baseline = new ShuttleHullBaseline();
        baseline.Build(Snapshot((0, 0, ShuttleHullBaseline.Structure, 2)));
        var state = baseline.Build(Snapshot((0, 0, ShuttleHullBaseline.Structure, 1)));
        Assert.That(state.ExpectedStructures, Is.EqualTo(2));
        Assert.That(state.MissingStructures, Is.EqualTo(1));
    }

    [Test]
    public void HugeSparseGridHasBoundedPayloadAndMissingCellWinsAggregation()
    {
        var baseline = new ShuttleHullBaseline();
        baseline.Build(Snapshot((-1000000, -10, ShuttleHullBaseline.Weapon, 1), (1000000, 10, ShuttleHullBaseline.Thruster, 1)));
        var state = baseline.Build(Snapshot((-999999, -10, ShuttleHullBaseline.Structure, 1), (1000000, 10, ShuttleHullBaseline.Thruster, 1)));
        Assert.Multiple(() =>
        {
            Assert.That(state.Width, Is.LessThanOrEqualTo(ShuttleHullBaseline.MaxResolution));
            Assert.That(state.Height, Is.LessThanOrEqualTo(ShuttleHullBaseline.MaxResolution));
            Assert.That(state.Cells.Length, Is.LessThanOrEqualTo(ShuttleHullBaseline.MaxResolution * ShuttleHullBaseline.MaxResolution));
            Assert.That(state.Cells[0] & 2, Is.EqualTo(2));
            Assert.That(state.MissingWeapons, Is.EqualTo(1));
        });
    }

    [Test]
    public void RepairAndNewLossAtDifferentPositionsAreNotHiddenByEqualTotals()
    {
        var baseline = new ShuttleHullBaseline();
        baseline.Build(Snapshot((0, 0, ShuttleHullBaseline.Weapon, 1), (1, 0, ShuttleHullBaseline.Weapon, 1)));
        baseline.Build(Snapshot((0, 0, ShuttleHullBaseline.Weapon, 1)));
        Assert.That(baseline.NewLosses.MissingWeapons, Is.EqualTo(1));
        var state = baseline.Build(Snapshot((1, 0, ShuttleHullBaseline.Weapon, 1)));
        Assert.That(state.MissingWeapons, Is.EqualTo(1));
        Assert.That(baseline.NewLosses.MissingWeapons, Is.EqualTo(1));
        baseline.Build(Snapshot((1, 0, ShuttleHullBaseline.Weapon, 1)));
        Assert.That(baseline.NewLosses.MissingWeapons, Is.Zero);
    }
}

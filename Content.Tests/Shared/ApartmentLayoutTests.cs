#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared._DeepLagoon.Apartments;
using NUnit.Framework;
using Robust.Shared.Maths;

namespace Content.Tests.Shared;

[TestFixture]
public sealed class ApartmentLayoutTests
{
    [Test]
    public void MapSceneValidatesActualFloorsFixturesAndExistingCorridors()
    {
        var template = Template();
        var scene = new ApartmentRoomScene { Width = 8, Height = 8 };
        foreach (var cell in new[] { new Vector2i(1, 1), new Vector2i(2, 1), new Vector2i(3, 1), new Vector2i(3, 2), new Vector2i(0, 1), new Vector2i(6, 6) })
            scene.Tiles.Add(new Content.Shared._DeepLagoon.Mapping.GridEditorItem { X = cell.X, Y = cell.Y });
        scene.Occupied.Add(new Vector2i(3, 2));
        scene.Blocked.Add(new Vector2i(3, 2));
        string? Validate(int x, int y) => ApartmentLayout.Validate(template,
            new[] { new ApartmentPlacement { Id = "added", Furniture = "chair", X = x, Y = y } }, Catalog, scene);
        Assert.That(Validate(3, 3), Does.Contain("стеной"));
        Assert.That(Validate(3, 2), Does.Contain("стационарным"));
        Assert.That(Validate(0, 1), Is.Null);
        Assert.That(Validate(2, 1), Is.Null);
        Assert.That(ApartmentLayout.Validate(template, Array.Empty<ApartmentPlacement>(), Catalog, scene), Is.Null);
    }

    [Test]
    public void MappedFurnitureMovesPersistWithoutCloningOrDeletingTheOriginal()
    {
        var template = Template();
        var original = new ApartmentPlacement { Id = "map_73", Furniture = "map_73", X = 5, Y = 5 };
        var definition = new ApartmentFurniturePrototype { ID = "map_73", ExistingOnly = true, BlocksMovement = false, MaxCount = 1 };
        ApartmentFurniturePrototype? Definitions(string id) => id == "map_73" ? definition : Catalog(id);
        var layout = template.Furniture.Select(p => p.Copy()).Append(original.Copy()).ToList();
        layout.Last().X = 3; layout.Last().Rotation = 2;
        Assert.That(ApartmentLayout.Validate(template, layout, Definitions, mapFurniture: new[] { original }), Is.Null);
        var delta = ApartmentLayout.Diff(template, layout, 1, new[] { original });
        Assert.That(delta.Changed.Single().Id, Is.EqualTo(original.Id));
        Assert.That(delta.Added, Is.Empty);
        Assert.That(ApartmentLayout.Resolve(template, delta, new[] { original }).Last().SameAs(layout.Last()), Is.True);
        Assert.That(ApartmentLayout.Validate(template, layout.Take(1).ToArray(), Definitions, mapFurniture: new[] { original }), Does.Contain("нельзя удалять"));
        definition.CanRotate = false;
        Assert.That(ApartmentLayout.Validate(template, layout, Definitions, mapFurniture: new[] { original }), Does.Contain("нельзя поворачивать"));
        definition.CanRotate = true;
        var clone = original.Copy(); clone.Id = "clone";
        Assert.That(ApartmentLayout.Validate(template, layout.Append(clone).ToArray(), Definitions, mapFurniture: new[] { original }), Does.Contain("каталоге"));
        layout.Last().Furniture = "chair";
        Assert.That(ApartmentLayout.Validate(template, layout, Definitions, mapFurniture: new[] { original }), Does.Contain("подменить"));
    }

    private static ApartmentTemplatePrototype Template() => new()
    {
        ID = "test", Width = 8, Height = 8, Catalog = new() { "chair", "table" },
        Furniture = new() { new() { Id = "base_chair", Furniture = "chair", X = 4, Y = 4 } },
    };
    private static ApartmentFurniturePrototype? Catalog(string id) => id switch
    {
        "chair" => new() { ID = id, MaxCount = 3, BlocksMovement = false },
        "table" => new() { ID = id, Size = new Vector2i(2, 1), MaxCount = 10 },
        _ => null,
    };

    [Test]
    public void PatchStoresOnlyFinalChangesAndRestoringBaselineCompactsIt()
    {
        var template = Template();
        var layout = template.Furniture.Select(p => p.Copy()).ToList();
        layout[0].X = 5;
        layout.Add(new() { Id = "added_table", Furniture = "table", X = 3, Y = 2 });
        var delta = ApartmentLayout.Diff(template, layout, 1);
        Assert.Multiple(() =>
        {
            Assert.That(delta.Changed, Has.Count.EqualTo(1));
            Assert.That(delta.Added, Has.Count.EqualTo(1));
            Assert.That(delta.Removed, Is.Empty);
            Assert.That(ApartmentLayout.Resolve(template, delta).Zip(layout).All(p => p.First.SameAs(p.Second)), Is.True);
        });
        var reset = ApartmentLayout.Diff(template, template.Furniture, 2);
        Assert.That(reset.Changed.Concat(reset.Added), Is.Empty);
    }

    [Test]
    public void RemovedBaselineDoesNotRespawnAndAddedIdsCannotImpersonateIt()
    {
        var template = Template();
        var delta = ApartmentLayout.Diff(template, Array.Empty<ApartmentPlacement>(), 1);
        Assert.That(delta.Removed, Is.EqualTo(new[] { "base_chair" }));
        Assert.That(ApartmentLayout.Resolve(template, delta), Is.Empty);
        delta.Added.Add(template.Furniture[0].Copy());
        Assert.Throws<InvalidOperationException>(() => ApartmentLayout.Resolve(template, delta));
    }

    [Test]
    public void TemplateVersionMismatchRequiresMigration()
    {
        var template = Template();
        var delta = ApartmentLayout.Diff(template, template.Furniture, 1);
        template.Version++;
        Assert.Throws<InvalidOperationException>(() => ApartmentLayout.Resolve(template, delta));
        Assert.That(delta.TemplateVersion, Is.EqualTo(1));
    }

    [Test]
    public void RotationBoundsOverlapExitAndCountAreValidated()
    {
        var template = Template();
        var a = new ApartmentPlacement { Id = "a", Furniture = "table", X = 6, Y = 4 };
        Assert.That(ApartmentLayout.Validate(template, new[] { a }, Catalog), Does.Contain("стеной"));
        a.Rotation = 1;
        Assert.That(ApartmentLayout.Validate(template, new[] { a }, Catalog), Is.Null);
        a.X = 1; a.Y = 1;
        Assert.That(ApartmentLayout.Validate(template, new[] { a }, Catalog), Does.Contain("прибытия"));
        a.X = 4; a.Y = 4;
        Assert.That(ApartmentLayout.Validate(template, new[] { a, template.Furniture[0] }, Catalog), Does.Contain("пересекаются"));
        var chairs = Enumerable.Range(0, 4).Select(i => new ApartmentPlacement { Id = $"chair_{i}", Furniture = "chair", X = 2 + i, Y = 2 }).ToArray();
        Assert.That(ApartmentLayout.Validate(template, chairs, Catalog), Does.Contain("экземпляров"));
    }

    [Test]
    public void FreeFloorCannotBeCutOffFromExit()
    {
        var template = Template();
        var wall = Enumerable.Range(1, 6).Select(y => new ApartmentPlacement { Id = $"table_{y}", Furniture = "table", X = 3, Y = y }).ToArray();
        Assert.That(ApartmentLayout.Validate(template, wall, Catalog), Does.Contain("проход"));
    }

    [Test]
    public void ArbitraryPrototypeAndBaselineReplacementAreRejected()
    {
        var template = Template();
        var replacement = template.Furniture[0].Copy(); replacement.Furniture = "table";
        Assert.That(ApartmentLayout.Validate(template, new[] { replacement }, Catalog), Does.Contain("подменить"));
        replacement.Id = "new"; replacement.Furniture = "ExplosiveBanana";
        Assert.That(ApartmentLayout.Validate(template, new[] { replacement }, Catalog), Does.Contain("каталоге"));
    }

    [Test]
    public void AuthoredMapOverlapsRemainValidButMovedFurnitureCannotUseThem()
    {
        var template = Template();
        var chair = new ApartmentPlacement { Id = "mapped-chair", Furniture = "chair", X = 4, Y = 4 };
        var table = new ApartmentPlacement { Id = "mapped-table", Furniture = "table", X = 4, Y = 4 };
        var baseline = new[] { chair, table };
        Assert.That(ApartmentLayout.Validate(template, baseline, Catalog, mapFurniture: baseline), Is.Null);
        var movedChair = chair.Copy(); movedChair.X = 5;
        Assert.That(ApartmentLayout.Validate(template, new[] { movedChair, table }, Catalog, mapFurniture: baseline), Does.Contain("пересекаются"));
        Assert.That(ApartmentLayout.Validate(template, new[] { table, movedChair }, Catalog, mapFurniture: baseline), Does.Contain("пересекаются"));
        var scene = new ApartmentRoomScene();
        for (var x = 1; x < 7; x++)
        for (var y = 1; y < 7; y++) scene.Tiles.Add(new Content.Shared._DeepLagoon.Mapping.GridEditorItem { X = x, Y = y });
        scene.Occupied.Add(new Vector2i(4, 4));
        Assert.That(ApartmentLayout.Validate(template, baseline, Catalog, scene, baseline), Is.Null);
        var added = new ApartmentPlacement { Id = "added-chair", Furniture = "chair", X = 4, Y = 4 };
        Assert.That(ApartmentLayout.Validate(template, new[] { chair, table, added }, Catalog, scene, baseline), Does.Contain("стационарным"));
    }
}

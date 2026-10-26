using System.Collections.Generic;
using System.Linq;
using Content.Shared._DeepLagoon.Apartments;
using NUnit.Framework;

namespace Content.Tests.Shared;

[TestFixture]
public sealed class ApartmentFurnitureStockTests
{
    [Test]
    public void LegacyPurchasesRetainTheirEntireCapacityWhenSofaSectionsAreGrouped()
    {
        var sofa = new ApartmentFurniturePrototype { ID = "sofa", MaxCount = 8, Price = 95 };
        var left = new ApartmentFurniturePrototype { ID = "left", Product = "sofa", MaxCount = 8, Price = 95 };
        Assert.That(ApartmentFurnitureStock.Capacity(sofa, new[] { sofa, left }, new Dictionary<string, int> { ["sofa"] = 8 },
            new HashSet<string> { "sofa", "left" }), Is.EqualTo(24));
        sofa.Price = 0; sofa.MaxCount = 4;
        Assert.That(ApartmentFurnitureStock.Capacity(sofa, new[] { sofa, left }, new Dictionary<string, int>(), new HashSet<string> { "left" }), Is.EqualTo(12));
    }

    [Test]
    public void SofaSectionsConsumeOneSharedStockAndLoadingDoesNotDependOnAnOnlineWalletCache()
    {
        var template = new ApartmentTemplatePrototype { Width = 32, Height = 32, Catalog = new() { "left", "right" } };
        ApartmentFurniturePrototype Definition(string id) => new() { ID = id, Product = "sofa", MaxCount = 8, AllowsOverlap = true, BlocksMovement = false };
        var layout = Enumerable.Range(0, 9).Select(i => new ApartmentPlacement { Id = "piece"+i, Furniture = i % 2 == 0 ? "left" : "right", X = 3, Y = 3 }).ToArray();
        Assert.That(ApartmentLayout.Validate(template, layout, Definition, stock: _ => 8), Does.Contain("экземпляров"));
        Assert.That(ApartmentLayout.Validate(template, layout, Definition, stock: _ => 16), Is.Null);
        Assert.That(ApartmentLayout.Validate(template, layout, Definition, validateStock: false), Is.Null);
    }

    [Test]
    public void OldSmallTemplateLimitIsIgnoredButTheExceptionalFourHundredObjectLimitIsEnforced()
    {
        var template = new ApartmentTemplatePrototype { Width = 32, Height = 32, MaxFurniture = 1, Catalog = new() { "rug" } };
        ApartmentFurniturePrototype Definition(string id) => new() { ID = id, MaxCount = 500, AllowsOverlap = true, BlocksMovement = false };
        var layout = Enumerable.Range(0, 400).Select(i => new ApartmentPlacement { Id = "rug"+i, Furniture = "rug", X = 3, Y = 3 }).ToArray();
        Assert.That(ApartmentLayout.Validate(template, layout, Definition), Is.Null);
        Assert.That(ApartmentLayout.Validate(template, layout.Append(new() { Id = "extra", Furniture = "rug", X = 3, Y = 3 }).ToArray(), Definition), Does.Contain("слишком много"));
    }
}

using System;
using System.Reflection;
using System.Text.Json;
using Content.Client._DeepLagoon.Mapping;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Mapping;
using NUnit.Framework;

namespace Content.Tests._DeepLagoon;

[TestFixture]
public sealed class GridEditorJsonTests
{
    [TestCase(0)]
    [TestCase(0x305)]
    public void StreamingItemsMatchExistingEncodingIncludingEscapesAndNonFiniteNumbers(int gasPorts)
    {
        var original = typeof(GridEditorSystem).GetMethod("ItemData", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<GridEditorItem, TguiData>>();
        var item = new GridEditorItem { Id = "quote\"\n\\\u2028", Kind = "decal", Prototype = "тест", X = 1.25f, Y = float.NaN,
            Rotation = float.PositiveInfinity, Color = "#00FF00FF", Z = -9, Anchored = true, Cleanable = true,
            Variant = 255, Flags = 3, Mirror = 4, PipeLayer = 2, GasPorts = gasPorts, EntityData = "state\n\"custom\"\\текст", EntityGroup = "group" };
        var expected = new TguiData().Array("items", new[] { original(item), original(item) }).ToString();
        var actual = new TguiData().Array("items", builder => GridEditorJson.AppendItems(builder, new[] { item, item })).ToString();
        Assert.That(actual, Is.EqualTo(expected));
        using var parsed = JsonDocument.Parse(actual);
        Assert.That(parsed.RootElement.GetProperty("items")[0].GetProperty("id").GetString(), Is.EqualTo(item.Id));
        Assert.That(new TguiData().Array("items", builder => GridEditorJson.AppendItems(builder, Array.Empty<GridEditorItem>())).ToString(), Is.EqualTo("{\"items\":[]}"));
    }
}

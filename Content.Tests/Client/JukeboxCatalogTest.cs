using System;
using System.Linq;
using Content.Client.Audio.Jukebox;
using NUnit.Framework;

namespace Content.Tests.Client;

[TestFixture]
public sealed class JukeboxCatalogTest
{
    [Test]
    public void HugeCatalogReturnsOnlyOnePageAndSearchesTheWholeLibrary()
    {
        var catalog = new JukeboxCatalog(Enumerable.Range(0, 100000)
            .Select(i => new JukeboxCatalog.Track($"track{i}", i == 99999 ? "Последний ТРЕК" : $"Song {i:D6}")).ToArray());
        var first = catalog.Browse(null, "", 0);
        Assert.That(first.Total, Is.EqualTo(100000));
        Assert.That(first.Rows.Length, Is.EqualTo(25));
        var found = catalog.Browse(null, "последний трек", 0);
        Assert.That(found.Rows.Single().Track, Is.EqualTo("track99999"));
        var last = catalog.Browse(null, "", int.MaxValue);
        Assert.That(last.Index, Is.EqualTo(3999));
        Assert.That(last.Rows.Length, Is.EqualTo(25));
        Assert.That(catalog.Browse(null, "no match", 100).Rows, Is.Empty);
    }

    [Test]
    public void PersonalAndQueuePagesRetainMissingIdsDuplicatesAndOriginalIndices()
    {
        var catalog = new JukeboxCatalog([new("song", "Song")]);
        var page = catalog.Browse([new("entry1", "song", 0), new("entry2", "song", 1), new("missing", "missing", 2)], "", 0);
        Assert.That(page.Rows.Select(e => e.Id), Is.EqualTo(new[] { "entry1", "entry2", "missing" }));
        Assert.That(page.SourceCount, Is.EqualTo(3));
        Assert.That(catalog.Browse(page.Rows, "missing", 0).Rows.Single().SourceIndex, Is.EqualTo(2));
        Assert.That(new JukeboxCatalog([]).RandomTrack(), Is.Null);
        Assert.That(catalog.RandomTrack(), Is.EqualTo("song"));
    }
}

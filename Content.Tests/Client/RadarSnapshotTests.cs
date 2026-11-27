using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Client._DeepLagoon.Voyages;
using Content.Shared._Mono.Radar;
using NUnit.Framework;

namespace Content.Tests.Client;

public sealed class RadarSnapshotTests
{
    [Test]
    public void ADispatchedRadarSnapshotOwnsItsBuffers()
    {
        var palette = new List<BlipConfig> { new() };
        var blips = new List<BlipNetData> { default };
        var missiles = new List<MissileVectorNetData> { default };
        var hits = new List<HitscanNetData> { default };
        var packet = new GiveBlipsEvent(palette, blips, missiles, hits, sampleTime: TimeSpan.FromSeconds(123));
        palette.Clear(); blips.Clear(); missiles.Clear(); hits.Clear();
        Assert.Multiple(() =>
        {
            Assert.That(packet.ConfigPalette, Has.Count.EqualTo(1)); Assert.That(packet.Blips, Has.Count.EqualTo(1));
            Assert.That(packet.Missiles, Has.Count.EqualTo(1)); Assert.That(packet.HitscanLines, Has.Count.EqualTo(1));
            Assert.That(packet.SampleTime, Is.EqualTo(TimeSpan.FromSeconds(123)));
        });
    }
    [Test]
    public void RadarFogFeathersBetweenSensorEnvelopesWithoutAHardPolygonEdge()
    {
        Vector2[] centers = [Vector2.Zero, new(600, 0)];
        Assert.That(VoyageFogMask.Opacity(Vector2.Zero, centers), Is.Zero);
        Assert.That(VoyageFogMask.Opacity(new(300, 0), centers), Is.Zero, "Overlapping explored areas stay clear.");
        var previous = 0f;
        for (var y = 320; y <= 640; y++)
        {
            var alpha = VoyageFogMask.Opacity(new(0, y), centers);
            Assert.That(alpha, Is.InRange(previous, previous + .01f)); previous = alpha;
        }
        Assert.That(previous, Is.EqualTo(1));
        Assert.That(VoyageFogMask.Opacity(new(5000, 5000), centers), Is.EqualTo(1));
    }
}

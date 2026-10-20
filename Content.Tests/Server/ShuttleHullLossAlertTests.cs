using System;
using Content.Server.Shuttles.Systems;
using Content.Shared.Shuttles.BUIStates;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class ShuttleHullLossAlertTests
{
    [Test]
    public void SingleNewLossKeepsItsNameAndCoalescedLossesDoNotNameOneObject()
    {
        var alert = new ShuttleHullLossAlert();
        alert.ObserveNewLosses(new ShuttleHullState { ExpectedThrusters = 2 }, new ShuttleHullState());
        alert.ObserveNewLosses(new ShuttleHullState { ExpectedThrusters = 2, MissingThrusters = 1 },
            new ShuttleHullState { MissingThrusters = 1, SingleMissingName = "port engine" });
        Assert.That(alert.SingleMissingName, Is.EqualTo("port engine"));
        alert.ObserveNewLosses(new ShuttleHullState { ExpectedThrusters = 2, MissingThrusters = 2 },
            new ShuttleHullState { MissingThrusters = 1, SingleMissingName = "starboard engine" });
        Assert.That(alert.Total, Is.EqualTo(2));
        Assert.That(alert.SingleMissingName, Is.Null);
    }

    [Test]
    public void InitialAndUnchangedLossesDoNotTriggerAlarm()
    {
        var alert = new ShuttleHullLossAlert();
        alert.ObserveNewLosses(new ShuttleHullState { MissingThrusters = 2 }, new ShuttleHullState { MissingThrusters = 2 });
        alert.ObserveNewLosses(new ShuttleHullState { MissingThrusters = 2 }, new ShuttleHullState());
        Assert.That(alert.Ready(TimeSpan.Zero), Is.False);
        Assert.That(alert.Total, Is.Zero);
    }

    [Test]
    public void NewLossesAccumulateWhileThrottledAndAreAcknowledgedOnce()
    {
        var alert = new ShuttleHullLossAlert();
        alert.ObserveNewLosses(new ShuttleHullState(), new ShuttleHullState());
        alert.ObserveNewLosses(new ShuttleHullState { MissingThrusters = 1 }, new ShuttleHullState { MissingThrusters = 1 });
        Assert.That(alert.Ready(TimeSpan.Zero), Is.True);
        alert.Acknowledge(TimeSpan.Zero);
        alert.ObserveNewLosses(new ShuttleHullState { MissingThrusters = 1, MissingWeapons = 1 }, new ShuttleHullState { MissingWeapons = 1 });
        alert.ObserveNewLosses(new ShuttleHullState { MissingThrusters = 1, MissingWeapons = 2, MissingStructures = 3 },
            new ShuttleHullState { MissingWeapons = 1, MissingStructures = 3 });
        Assert.That(alert.Ready(TimeSpan.FromSeconds(5)), Is.False);
        Assert.That(alert.Ready(TimeSpan.FromSeconds(6)), Is.True);
        Assert.That(alert.Total, Is.EqualTo(5));
        alert.Acknowledge(TimeSpan.FromSeconds(6));
        Assert.That(alert.Ready(TimeSpan.FromSeconds(10)), Is.False);
    }

    [Test]
    public void RepairDoesNotAlarmAndLaterLossIsNew()
    {
        var alert = new ShuttleHullLossAlert();
        alert.ObserveNewLosses(new ShuttleHullState { ExpectedWeapons = 1, MissingWeapons = 1 }, new ShuttleHullState { MissingWeapons = 1 });
        alert.ObserveNewLosses(new ShuttleHullState { ExpectedWeapons = 1 }, new ShuttleHullState());
        Assert.That(alert.Total, Is.Zero);
        alert.ObserveNewLosses(new ShuttleHullState { ExpectedWeapons = 1, MissingWeapons = 1 }, new ShuttleHullState { MissingWeapons = 1 });
        Assert.That(alert.Weapons, Is.EqualTo(1));
    }

    [TestCase(10000, 1, 1)]
    [TestCase(10, 1, 10)]
    [TestCase(3, 1, 34)]
    [TestCase(1, 1, 100)]
    public void PercentageRoundsUpWithoutFloatingPointErrors(int expected, int missing, int percent)
    {
        var alert = new ShuttleHullLossAlert();
        alert.ObserveNewLosses(new ShuttleHullState { ExpectedStructures = expected }, new ShuttleHullState());
        alert.ObserveNewLosses(new ShuttleHullState { ExpectedStructures = expected, MissingStructures = missing },
            new ShuttleHullState { MissingStructures = missing });
        Assert.That(alert.DamagePercent, Is.EqualTo(percent));
        Assert.That(alert.Ready(TimeSpan.Zero), Is.True);
    }

    [Test]
    public void CompleteRepairCancelsPendingAlarmBeforeConsoleCanReport()
    {
        var alert = new ShuttleHullLossAlert();
        alert.ObserveNewLosses(new ShuttleHullState { ExpectedWeapons = 1 }, new ShuttleHullState());
        alert.ObserveNewLosses(new ShuttleHullState { ExpectedWeapons = 1, MissingWeapons = 1 }, new ShuttleHullState { MissingWeapons = 1 });
        Assert.That(alert.Ready(TimeSpan.Zero), Is.True);
        alert.ObserveNewLosses(new ShuttleHullState { ExpectedWeapons = 1 }, new ShuttleHullState());
        Assert.That(alert.Total, Is.Zero);
        Assert.That(alert.DamagePercent, Is.Zero);
        Assert.That(alert.Ready(TimeSpan.FromSeconds(10)), Is.False);
    }
}

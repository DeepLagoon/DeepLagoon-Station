using System;
using System.Numerics;
using Content.Server._NF.PublicTransit;
using Content.Server._NF.PublicTransit.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._NF.CCVar;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.TextScreen;
using Content.Shared.Timing;
using Robust.Shared.Configuration;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Localization;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

// Install deterministic bus state without starting real FTL trips in this fixture.
#pragma warning disable RA0002
[TestFixture]
public sealed class BusScreenTests
{
    [Test]
    public async Task ScreensFollowGridRouteFlightAndServiceAvailability()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var transit = entities.System<PublicTransitSystem>();
            var shuttles = entities.System<ShuttleSystem>();
            var maps = entities.System<MapSystem>();
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            var timing = pair.Server.ResolveDependency<IGameTiming>();
            var now = timing.CurTime;
            // Enable before registering stops, so this fixture installs its own bus.
            config.SetCVar(NFCCVars.PublicTransit, true);
            transit.StationList.Clear();
            var first = map.Grid.Owner;
            var second = maps.CreateGridEntity(map.MapId).Owner;
            var third = maps.CreateGridEntity(map.MapId).Owner;
            var outside = maps.CreateGridEntity(map.MapId).Owner;
            foreach (var grid in new[] { first, second, third, outside })
                maps.SetTile(grid, entities.GetComponent<MapGridComponent>(grid), new Vector2i(0, 0), map.Tile.Tile);
            foreach (var grid in new[] { first, second, third })
                Assert.That(transit.RegisterStop(grid), Is.True);

            var busGrid = maps.CreateGridEntity(map.MapId).Owner;
            entities.EnsureComponent<ShuttleComponent>(busGrid);
            var bus = entities.AddComponent<TransitShuttleComponent>(busGrid);
            bus.CurrentStation = first;
            bus.NextStation = second;
            bus.NextTransfer = now + TimeSpan.FromSeconds(30);
            transit.Counter = 2;
            transit.FlyTime = 50;
            config.SetCVar(NFCCVars.PublicTransitWaitTime, 60f);
            var flight = TimeSpan.FromSeconds(shuttles.DefaultStartupTime + Math.Max(50, shuttles.DefaultArrivalTime));

            Assert.That(transit.TryGetBusArrival(first, out _, out var here), Is.True);
            Assert.That(here, Is.True);
            Assert.That(transit.TryGetBusArrival(second, out var secondArrival, out here), Is.True);
            Assert.That(here, Is.False);
            Assert.That(secondArrival, Is.EqualTo(bus.NextTransfer + flight));
            Assert.That(transit.TryGetBusArrival(third, out var thirdArrival, out _), Is.True);
            Assert.That(thirdArrival, Is.EqualTo(secondArrival + TimeSpan.FromSeconds(110)));
            Assert.That(transit.TryGetBusArrival(outside, out _, out _), Is.False);

            EntityUid Screen(EntityUid grid) => entities.SpawnEntity("DLBusScreen", new EntityCoordinates(grid, new Vector2(.5f, .5f)));
            var atFirst = Screen(first);
            var atSecond = Screen(second);
            var atOutside = Screen(outside);
            var appearance = entities.System<SharedAppearanceSystem>();

            Assert.That(appearance.TryGetData<TimeSpan>(atSecond, TextScreenVisuals.TargetTime, out var target), Is.True);
            Assert.That(target, Is.EqualTo(secondArrival));
            Assert.That(appearance.TryGetData<TimeSpan>(atFirst, TextScreenVisuals.TargetTime, out target), Is.True);
            Assert.That(target, Is.EqualTo(TimeSpan.Zero));
            Assert.That(appearance.TryGetData<string>(atFirst, TextScreenVisuals.ScreenText, out var label), Is.True);
            Assert.That(label, Is.EqualTo(Loc.GetString("dl-bus-screen-arrived")));
            Assert.That(appearance.TryGetData<string>(atOutside, TextScreenVisuals.ScreenText, out label), Is.True);
            Assert.That(label, Is.EqualTo(Loc.GetString("dl-bus-screen-no-service")));
            var device = entities.GetComponent<DeviceNetworkComponent>(atSecond);
            Assert.That(device.ReceiveFrequencyId!.Value.Id, Is.EqualTo("DLBusScreen"));

            bus.Destination = second;
            bus.NextStation = third;
            transit.Counter = 3;
            var ftl = entities.AddComponent<FTLComponent>(busGrid);
            ftl.TravelTime = 50;
            ftl.StateTime = StartEndTime.FromStartDuration(now, 10);
            foreach (var state in new[] { FTLState.Starting, FTLState.Travelling, FTLState.Arriving })
            {
                ftl.State = state;
                Assert.That(transit.TryGetBusArrival(second, out var arrival, out here), Is.True);
                Assert.That(here, Is.False);
                var remaining = state == FTLState.Starting ? Math.Max(50, shuttles.DefaultArrivalTime) :
                    state == FTLState.Travelling ? shuttles.DefaultArrivalTime : 0;
                Assert.That(arrival, Is.EqualTo(ftl.StateTime.End + TimeSpan.FromSeconds(remaining)));
                Assert.That(transit.TryGetBusArrival(first, out _, out here), Is.True);
                Assert.That(here, Is.False, "The departed bus must no longer be shown at the previous stop.");
            }
            ftl.State = FTLState.Cooldown;
            bus.CurrentStation = second;
            bus.Destination = null;
            Assert.That(transit.TryGetBusArrival(second, out _, out here), Is.True);
            Assert.That(here, Is.True);
            transit.UnregisterStop(second);
            Assert.That(transit.TryGetBusArrival(second, out _, out _), Is.False);
            entities.DeleteEntity(busGrid);
            Assert.That(transit.TryGetBusArrival(first, out _, out _), Is.False);
            config.SetCVar(NFCCVars.PublicTransit, false);
            Assert.That(transit.TryGetBusArrival(first, out _, out _), Is.False);
        });
        await pair.CleanReturnAsync();
    }
}

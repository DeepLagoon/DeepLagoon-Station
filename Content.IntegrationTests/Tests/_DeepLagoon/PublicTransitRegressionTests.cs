using System;
using Content.Server._DeepLagoon.Mapping;
using Content.Server._NF.PublicTransit;
using Content.Server._NF.PublicTransit.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server.Shuttles.Systems;
using Content.Shared._NF.CCVar;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._DeepLagoon;

// Exercise expired departure timers and cached destinations on the real server systems.
#pragma warning disable RA0002
[TestFixture]
public sealed class PublicTransitRegressionTests
{
    [Test]
    public async Task MappedBusReachesTheResidentialStopWithDefaultTimings()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        EntityUid residential = default, residentialMap = default, busGrid = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<MapSystem>();
            var transit = entities.System<PublicTransitSystem>();
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(NFCCVars.PublicTransit, false);
            transit.StationList.Clear();
            transit.Counter = 0;
            entities.EnsureComponent<FTLDestinationComponent>(map.MapUid).Enabled = true;
            transit.RegisterStop(map.Grid);

            residentialMap = maps.CreateMap(out var residentialId);
            Assert.That(entities.System<MapLoaderSystem>().TryLoadGridTracked(residentialId,
                new ResPath("/Maps/_DeepLagoon/Apartments/residential_stop.yml"), out var loaded), Is.True);
            residential = loaded!.Value.Owner;
            entities.EnsureComponent<FTLDestinationComponent>(residentialMap).Enabled = true;
            transit.RegisterStop(residential);
            // The shipped bus and the 10-second travel/dwell defaults reproduced
            // the cooldown refusal that consumed the residential stop on dev.
            config.SetCVar(NFCCVars.PublicTransit, true);
            var buses = entities.EntityQueryEnumerator<TransitShuttleComponent>();
            Assert.That(buses.MoveNext(out busGrid, out _), Is.True);
            Assert.That(entities.HasComponent<FTLComponent>(busGrid), Is.True);
        });

        await PoolManager.WaitUntil(pair.Server, async () =>
        {
            var reached = false;
            await pair.Server.WaitAssertion(() => reached =
                pair.Server.EntMan.GetComponent<TransitShuttleComponent>(busGrid).CurrentStation == residential);
            return reached;
        }, maxTicks: 6000, tickStep: 30);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<TransformComponent>(busGrid).MapUid, Is.EqualTo(residentialMap));
            Assert.That(entities.GetComponent<TransitShuttleComponent>(busGrid).Destination, Is.Null);
            var docks = entities.System<DockingSystem>().GetDocks(busGrid);
            Assert.That(docks.Exists(dock => dock.Comp.Docked && dock.Comp.DockedWith is { } other &&
                entities.GetComponent<TransformComponent>(other).GridUid == residential), Is.True,
                "The mapped bus must dock at the residential stop, not just reach its map.");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FlightAndCooldownDoNotSkipTheResidentialStop()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<MapSystem>();
            var transit = entities.System<PublicTransitSystem>();
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            var now = pair.Server.ResolveDependency<IGameTiming>().CurTime;
            var destinationMap = maps.CreateMap(out var destinationId);
            entities.AddComponent<FTLDestinationComponent>(destinationMap).Enabled = true;
            EntityUid Stop()
            {
                var grid = maps.CreateGridEntity(destinationId);
                maps.SetTile(grid, new Vector2i(0, 0), map.Tile.Tile);
                return grid.Owner;
            }
            var first = Stop();
            var residential = Stop();
            var third = Stop();
            transit.StationList.Clear();
            foreach (var stop in new[] { first, residential, third })
                transit.RegisterStop(stop);
            entities.EnsureComponent<ShuttleComponent>(map.Grid);
            var bus = entities.AddComponent<TransitShuttleComponent>(map.Grid);
            config.SetCVar(NFCCVars.PublicTransit, true);
            config.SetCVar(NFCCVars.PublicTransitWaitTime, 60f);
            transit.Counter = 2;
            bus.CurrentStation = first;
            bus.NextStation = residential;
            bus.Destination = first;
            bus.NextTransfer = now - TimeSpan.FromSeconds(1);
            var ftl = entities.AddComponent<FTLComponent>(map.Grid);
            foreach (var state in new[] { FTLState.Available, FTLState.Starting, FTLState.Travelling, FTLState.Arriving, FTLState.Cooldown })
            {
                ftl.State = state;
                transit.Update(0);
                Assert.That(bus.NextStation, Is.EqualTo(residential), $"Must retain the residential stop during {state}.");
                Assert.That(bus.Destination, Is.EqualTo(first));
                Assert.That(transit.Counter, Is.EqualTo(2));
                Assert.That(bus.NextTransfer, Is.LessThan(now));
            }

            entities.RemoveComponent<FTLComponent>(map.Grid);
            transit.Update(0);
            Assert.That(bus.Destination, Is.EqualTo(residential));
            Assert.That(bus.NextStation, Is.EqualTo(third));
            Assert.That(entities.GetComponent<FTLComponent>(map.Grid).State, Is.EqualTo(FTLState.Starting));
            Assert.That(entities.System<SharedTransformSystem>().GetMapId(
                entities.GetComponent<FTLComponent>(map.Grid).TargetCoordinates), Is.EqualTo(destinationId));

            entities.RemoveComponent<FTLComponent>(map.Grid);
            var arrival = new FTLCompletedEvent(map.Grid, destinationMap);
            entities.EventBus.RaiseLocalEvent(map.Grid, ref arrival);
            Assert.That(bus.CurrentStation, Is.EqualTo(residential));
            Assert.That(bus.Destination, Is.Null);
            Assert.That(bus.NextTransfer, Is.EqualTo(now + TimeSpan.FromSeconds(60)));
            transit.Update(0);
            Assert.That(entities.HasComponent<FTLComponent>(map.Grid), Is.False, "Passengers get the full dwell time after arrival.");

            // A longer drive cooldown must not consume the following stop either.
            bus.NextTransfer = now;
            entities.AddComponent<FTLComponent>(map.Grid).State = FTLState.Cooldown;
            transit.Update(0);
            Assert.That(bus.NextStation, Is.EqualTo(third));
            entities.RemoveComponent<FTLComponent>(map.Grid);
            transit.Update(0);
            Assert.That(bus.Destination, Is.EqualTo(third));
            entities.DeleteEntity(map.Grid);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingCachedStopIsReplacedWithoutSkippingTheFollowingStop(bool unregister)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<MapSystem>();
            var transit = entities.System<PublicTransitSystem>();
            entities.AddComponent<FTLDestinationComponent>(map.MapUid).Enabled = true;
            var removed = maps.CreateGridEntity(map.MapId).Owner;
            var residential = maps.CreateGridEntity(map.MapId).Owner;
            var third = maps.CreateGridEntity(map.MapId).Owner;
            foreach (var grid in new[] { removed, residential, third })
                maps.SetTile(grid, entities.GetComponent<MapGridComponent>(grid), new Vector2i(0, 0), map.Tile.Tile);
            transit.StationList.Clear();
            foreach (var grid in new[] { removed, residential, third })
                transit.RegisterStop(grid);
            entities.EnsureComponent<ShuttleComponent>(map.Grid);
            var bus = entities.AddComponent<TransitShuttleComponent>(map.Grid);
            pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(NFCCVars.PublicTransit, true);
            bus.NextStation = removed;
            transit.Counter = 1;
            if (unregister)
                transit.UnregisterStop(removed);
            entities.DeleteEntity(removed);
            Assert.DoesNotThrow(() => transit.Update(0));
            Assert.That(bus.Destination, Is.EqualTo(residential));
            Assert.That(bus.NextStation, Is.EqualTo(third));
            Assert.That(entities.HasComponent<FTLComponent>(map.Grid), Is.True);

            entities.RemoveComponent<FTLComponent>(map.Grid);
            entities.DeleteEntity(residential);
            entities.DeleteEntity(third);
            bus.NextTransfer = TimeSpan.Zero;
            Assert.DoesNotThrow(() => transit.Update(0));
            Assert.That(bus.NextStation, Is.EqualTo(EntityUid.Invalid));
            Assert.That(entities.HasComponent<FTLComponent>(map.Grid), Is.False);
            Assert.That(transit.StationList, Is.Empty);
            entities.DeleteEntity(map.Grid);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RefusedTripRetainsItsStopAndMissingMapsDoNotThrow()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<MapSystem>();
            var transit = entities.System<PublicTransitSystem>();
            var shuttles = entities.System<ShuttleSystem>();
            var dest = entities.AddComponent<FTLDestinationComponent>(map.MapUid);
            dest.Enabled = false;
            var stop = maps.CreateGridEntity(map.MapId).Owner;
            maps.SetTile(stop, entities.GetComponent<MapGridComponent>(stop), new Vector2i(0, 0), map.Tile.Tile);
            transit.StationList.Clear();
            transit.RegisterStop(stop);
            var shuttle = entities.EnsureComponent<ShuttleComponent>(map.Grid);
            var bus = entities.AddComponent<TransitShuttleComponent>(map.Grid);
            pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(NFCCVars.PublicTransit, true);
            bus.NextStation = stop;
            transit.Counter = 1;
            transit.Update(0);
            Assert.That(bus.NextStation, Is.EqualTo(stop));
            Assert.That(transit.Counter, Is.EqualTo(1));
            Assert.That(bus.Destination, Is.Null);
            Assert.That(entities.HasComponent<FTLComponent>(map.Grid), Is.False);
            Assert.That(bus.NextTransfer, Is.GreaterThan(pair.Server.ResolveDependency<IGameTiming>().CurTime));

            var detached = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            Assert.DoesNotThrow(() => shuttles.FTLToDock(map.Grid, shuttle, detached));
            entities.DeleteEntity(detached);
            Assert.DoesNotThrow(() => shuttles.FTLToDock(map.Grid, shuttle, detached));
            Assert.That(entities.HasComponent<FTLComponent>(map.Grid), Is.False);

            dest.Enabled = true;
            bus.NextTransfer = TimeSpan.Zero;
            transit.Update(0);
            Assert.That(bus.Destination, Is.EqualTo(stop));
            Assert.That(entities.HasComponent<FTLComponent>(map.Grid), Is.True);
            entities.DeleteEntity(map.Grid);
        });
        await pair.CleanReturnAsync();
    }
}

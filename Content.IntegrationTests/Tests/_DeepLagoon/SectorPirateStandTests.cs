using Content.Shared._Mono.Ships;
using Content.Shared._DeepLagoon.Voyages;
using Content.Server.Shuttles.Components;
#if DEBUG
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Economy;
using Content.Server._DeepLagoon.Voyages;
using Content.Server._NF.Bank;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class SectorPirateStandTests
{
    [TestCase("ordinary")]
    [TestCase("legacy")]
    [TestCase("marked")]
    public async Task DebugStandProvidesAnOwnedArmedShipAndActuallySpawnsAPirateForTheSelectedOrder(string orderKind)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var server = pair.Server; var e = server.EntMan; var home = await pair.CreateTestMap();
        var player = await server.AddDummySession("pirate_test");
        var prefs = server.ResolveDependency<IServerPreferencesManager>();
        await PoolManager.WaitUntil(server, () => prefs.TryGetCachedPreferences(player.UserId, out _));
        var sectors = e.System<SectorExpeditionSystem>(); var voyages = e.System<VoyageSystem>();
        EntityUid actor = default;
        await server.WaitAssertion(() =>
        {
            server.CfgMan.SetCVar(SectorEconomyCVars.Enabled, true); server.CfgMan.SetCVar(SectorExpeditionCVars.Enabled, true);
            actor = e.SpawnEntity("MobHumanDummy", home.GridCoords); e.EnsureComponent<BankAccountComponent>(actor);
            e.EnsureComponent<Content.Shared.Mobs.Components.MobStateComponent>(actor);
            server.PlayerMan.SetAttachedEntity(player, actor); e.System<BankSystem>().SetConfirmedBankBalance(actor, 100000);
        });
        await PoolManager.WaitUntil(server, () => voyages.Ready, 3600);
        Task setup = Task.CompletedTask;
        await server.WaitAssertion(() => setup = sectors.PreparePirateTest(player, actor));
        for (var i = 0; !setup.IsCompleted && i < 600; i++) { await server.WaitRunTicks(5); await Task.Delay(10); }
        Assert.That(setup.IsCompletedSuccessfully, Is.True);
        await server.WaitAssertion(() =>
        {
            var ship = e.GetComponent<TransformComponent>(actor).GridUid!.Value;
            Assert.That(ship, Is.Not.EqualTo(home.Grid.Owner));
            Assert.That(e.GetComponent<ShipOwnershipComponent>(ship).OwnerUserId, Is.EqualTo(player.UserId));
            Assert.That(e.EntityQuery<Content.Server._Mono.FireControl.FireControllableComponent, TransformComponent>().Any(g => g.Item2.GridUid == ship), Is.True);
            Assert.That(e.EntityQuery<FTLDriveComponent, TransformComponent>().Any(g => g.Item2.GridUid == ship), Is.True);
            var console = e.EntityQuery<Content.Shared.Salvage.Expeditions.SalvageExpeditionConsoleComponent, TransformComponent>().Single(g => g.Item2.GridUid == ship).Item1.Owner;
            Assert.That(e.HasComponent<Content.Server.Power.Components.ApcPowerReceiverComponent>(console), Is.False);
            var db = server.ResolveDependency<IServerDbManager>(); var tasks = server.ResolveDependency<ITaskManager>();
            var board = db.ReadSectorResearchAsync(voyages.WorldSnapshot.Cycle, player.UserId.UserId, 0); tasks.BlockWaitOnTask(board);
            Assert.That(board.GetAwaiter().GetResult().Orders.Any(o => o.Buyer == "Испытательный рейс · один пиратский корабль" && o.DebugFunded), Is.True);
        });
        var tasks = server.ResolveDependency<ITaskManager>(); var db = server.ResolveDependency<IServerDbManager>();
        EntityUid training = default, terminal = default; string destination = ""; Guid selectedOrder = Guid.NewGuid();
        await server.WaitAssertion(() =>
        {
            var privateFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            var encounters = (System.Collections.Generic.Dictionary<string, int>)typeof(SectorExpeditionSystem).GetField("_testEncounters", privateFlags)!.GetValue(sectors)!;
            var originallyPrepared = encounters.Keys.Single();
            var graph = voyages.RouteGraph; var visible = VoyageVisibility.VisibleSystems(graph);
            destination = graph.Systems.First(s => s.Id != originallyPrepared && visible.Contains(s.Id) &&
                !SectorResearchDefinition.Settled(s.Id) && graph.SurveyPath(s.Id).Length > 1).Id;
            training = e.GetComponent<TransformComponent>(actor).GridUid!.Value;
            terminal = e.EntityQuery<Content.Shared.Salvage.Expeditions.SalvageExpeditionConsoleComponent, TransformComponent>().Single(g => g.Item2.GridUid == training).Item1.Owner;
            foreach (var (power, transform) in e.EntityQuery<Content.Server.Power.Components.ApcPowerReceiverComponent, TransformComponent>())
                if (transform.GridUid == training) { power.NeedsPower = false; power.Powered = true; }
            foreach (var (drive, transform) in e.EntityQuery<FTLDriveComponent, TransformComponent>())
                if (transform.GridUid == training) drive.Cooldown = 0;
            var board = db.ReadSectorResearchAsync(voyages.WorldSnapshot.Cycle, player.UserId.UserId, 0); tasks.BlockWaitOnTask(board);
            Assert.That(board.GetAwaiter().GetResult().Orders.Any(o => o.DebugPirateTest), Is.True);
            var buyer = orderKind == "legacy" ? "Испытательный рейс · один пиратский корабль" : "Другой заказ в другом секторе";
            var publication = db.PublishSectorResearchAsync(new(selectedOrder, voyages.WorldSnapshot.Cycle, destination, buyer,
                20000, 20000, Guid.Empty, 0, 0, 0, SectorResearchDefinition.Count(destination, voyages.WorldSnapshot.Seed), new(),
                Automatic: true, DebugFunded: true, DebugPirateTest: orderKind == "marked"));
            tasks.BlockWaitOnTask(publication); publication.GetAwaiter().GetResult();
            if (orderKind != "ordinary")
            {
                // The durable order alone must restore its encounter after losing all stand metadata.
                encounters.Clear();
                ((System.Collections.Generic.HashSet<EntityUid>)typeof(SectorExpeditionSystem).GetField("_testShips", privateFlags)!.GetValue(sectors)!).Clear();
            }
            e.System<SharedUserInterfaceSystem>().OpenUi(terminal, Content.Shared.Salvage.Expeditions.SalvageConsoleUiKey.Expedition, actor);
            typeof(SectorExpeditionSystem).GetMethod("OnRequest", privateFlags)!.Invoke(sectors,
                [new SectorExpeditionRequest { Console = e.GetNetEntity(terminal), Action = SectorExpeditionAction.AcceptResearch, Sector = destination, Order = selectedOrder }, new Robust.Shared.GameObjects.EntitySessionEventArgs(player)]);
        });
        var arrived = false;
        for (var i = 0; !arrived && i < 1000; i++)
        {
            await server.WaitAssertion(() =>
            {
                var map = e.GetComponent<TransformComponent>(training).MapUid;
                arrived = map != null && e.HasComponent<SectorExpeditionMapComponent>(map) &&
                    (!e.TryGetComponent<FTLComponent>(training, out var ftl) || ftl.State == Content.Shared.Shuttles.Systems.FTLState.Cooldown);
            });
            if (!arrived) { await server.WaitRunTicks(10); await Task.Delay(5); }
        }
        Assert.That(arrived, Is.True, "The actual selected order must start FTL and arrive in its destination.");
        await server.WaitAssertion(() =>
        {
            var map = e.GetComponent<TransformComponent>(training).MapUid!.Value;
            Assert.That(e.GetComponent<SectorExpeditionMapComponent>(map).Sector, Is.EqualTo(destination));
            var cores = e.EntityQuery<MetaDataComponent, TransformComponent>().Where(v => v.Item2.MapUid == map && v.Item1.EntityPrototype?.ID == "NpcStationAiAttackerSmart").ToArray();
            Assert.That(cores, Has.Length.EqualTo(1), "Selecting a different or restored order on the stand still requires one real pirate.");
            var pirateGrid = cores[0].Item2.GridUid!.Value;
            var iff = e.GetComponent<IFFComponent>(pirateGrid);
            Assert.That(iff.Color, Is.EqualTo(Robust.Shared.Maths.Color.Red));
            Assert.That((iff.Flags & (IFFFlags.Hide | IFFFlags.HideLabel | IFFFlags.HideLabelAlways)), Is.EqualTo(IFFFlags.None),
                "A cold pirate at arrival range must not vanish behind thermal-only detection.");
            Assert.That(e.EntityQuery<Content.Server._Mono.FireControl.FireControllableComponent, TransformComponent>().Any(g => g.Item2.GridUid == pirateGrid), Is.True);
        });
        await pair.CleanReturnAsync();
    }
}
#endif

using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Content.Server._DeepLagoon.Apartments;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.GameTicking;
using Content.Shared.Preferences;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentTemplateShopTests
{
    [TestPrototypes]
    private const string Templates = @"
- type: apartmentTemplate
  id: ApartmentShopPaidTest
  name: dl-apartment-basic
  price: 10
  map: /Maps/_DeepLagoon/Apartments/apartments_basic.yml
  width: 11
  height: 16
  arrival: '4,8'
  reservedCells: ['3,12', '4,12', '5,12', '3,13', '4,13', '5,13', '3,14', '4,14', '5,14', '4,10', '4,11']
  catalog: [DLAptChair, DLAptPlant]
- type: apartmentTemplate
  id: ApartmentShopExpensiveTest
  name: dl-apartment-basic
  price: 100
  map: /Maps/_DeepLagoon/Apartments/apartments_basic.yml
  width: 11
  height: 16
  arrival: '4,8'
  reservedCells: ['3,12', '4,12', '5,12', '3,13', '4,13', '5,13', '3,14', '4,14', '5,14', '4,10', '4,11']
  catalog: [DLAptChair]
";
    [Test]
    public async Task LegacyMigrationLobbyEditingAndPaidSelectionArePersistentAndReplaySafe()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        var outside = await pair.CreateTestMap();
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        EntityUid body = default, paidGrid = default;
        Guid replay = default;
        int oldNumber = 0, migratedNumber = 0, paidNumber = 0;
        string plantId = "";
        Vector2 moved = default;
        void Show(ApartmentSystem system, ICommonSession session) => typeof(ApartmentSystem).GetMethod("PublishShop", flags)!.Invoke(system, new object[] { session, "" });
        Guid Token(ApartmentSystem system, ICommonSession session) => ((Dictionary<NetUserId, Guid>) typeof(ApartmentSystem).GetField("_shopTokens", flags)!.GetValue(system)!)[session.UserId];
        void Action(ApartmentSystem system, ICommonSession session, string action, string template = "", Guid? token = null) =>
            typeof(ApartmentSystem).GetMethod("ShopAction", flags)!.Invoke(system, new object[] { new ApartmentShopAction(token ?? Token(system, session), action, template), new EntitySessionEventArgs(session) });
        IApartmentLayoutStore Store(ApartmentSystem system) => (IApartmentLayoutStore) typeof(ApartmentSystem).GetProperty("Store", flags)!.GetValue(system)!;
        long Balance()
        {
            var task = pair.Server.ResolveDependency<IServerDbManager>().GetLagoonCoinsAsync(pair.Server.PlayerMan.Sessions.Single().UserId);
            pair.Server.ResolveDependency<ITaskManager>().BlockWaitOnTask(task);
#pragma warning disable RA0004 // The task has completed through the engine task manager.
            return task.Result;
#pragma warning restore RA0004
        }
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan; var session = pair.Server.PlayerMan.Sessions.Single();
            var system = entities.System<ApartmentSystem>();
            oldNumber = system.Number(session.UserId);
            Assert.That(Store(system).Save(session.UserId.UserId, new ApartmentDelta { Template = "DLApartmentStudio", TemplateVersion = 1, Revision = 1 }, 0), Is.True);
            Show(system, session);
            Assert.That(Store(system).Load(session.UserId.UserId)!.Template, Is.EqualTo("DLApartmentBasic"));
            migratedNumber = system.Number(session.UserId);
            Assert.That(migratedNumber, Is.Not.EqualTo(oldNumber)); Assert.That(system.NumberOwner(oldNumber), Is.Null);
            Action(system, session, "edit");
            var instance = system.Instances[session.UserId];
            var layout = ((List<ApartmentPlacement>) typeof(ApartmentSystem).GetMethod("CurrentLayout", flags)!.Invoke(system, new object[] { instance })!);
            plantId = instance.MapFurniture.First(item => item.Value.Prototype == "PottedPlant2").Key;
            var scene = (ApartmentRoomScene) typeof(ApartmentSystem).GetMethod("CaptureRoom", flags)!.Invoke(system, new object[] { instance })!;
            var free = scene.Tiles.Select(tile => new Vector2i((int) tile.X, (int) tile.Y)).First(cell => !scene.Occupied.Contains(cell)
                && !instance.Template.ReservedCells.Contains(cell) && cell != instance.Template.Arrival && !layout.Any(p => p.X == cell.X && p.Y == cell.Y));
            var plant = layout.Single(p => p.Id == plantId); plant.X = free.X; plant.Y = free.Y;
            moved = new Vector2(free.X + .5f, free.Y + .5f);
            var editorToken = ((Dictionary<NetUserId, Guid>) typeof(ApartmentSystem).GetField("_editors", flags)!.GetValue(system)!)[session.UserId];
            Assert.That(session.AttachedEntity, Is.Null);
            typeof(ApartmentSystem).GetMethod("OnApply", flags)!.Invoke(system, new object[] {
                new ApartmentApplyEvent(editorToken, instance.Delta.Revision, layout), new EntitySessionEventArgs(session) });
            Assert.That(Store(system).Load(session.UserId.UserId)!.Changed.Single(p => p.Id == plantId).X, Is.EqualTo(free.X));
            pair.Server.ResolveDependency<ITaskManager>().BlockWaitOnTask(entities.System<Content.Server._DeepLagoon.Currency.LagoonCoinSystem>().Grant(session.UserId, 25, "shop-test", null));
            Assert.That(Balance(), Is.EqualTo(25));
            Show(system, session); replay = Token(system, session);
            Action(system, session, "select", "ApartmentShopPaidTest");
            Action(system, session, "select", "ApartmentShopPaidTest", replay);
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan; var session = pair.Server.PlayerMan.Sessions.Single(); var system = entities.System<ApartmentSystem>();
            Assert.That(Store(system).Owned(session.UserId.UserId, "ApartmentShopPaidTest"), Is.True);
            Assert.That(Store(system).Load(session.UserId.UserId)!.Template, Is.EqualTo("ApartmentShopPaidTest"));
            Assert.That(Balance(), Is.EqualTo(15));
            paidNumber = system.Number(session.UserId); Assert.That(paidNumber, Is.Not.EqualTo(migratedNumber));
            Assert.That(system.NumberOwner(migratedNumber), Is.Null);
            Action(system, session, "select", "ApartmentShopPaidTest", replay);
            Assert.That(Balance(), Is.EqualTo(15));
            Show(system, session); Action(system, session, "select", "ApartmentShopExpensiveTest");
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan; var session = pair.Server.PlayerMan.Sessions.Single(); var system = entities.System<ApartmentSystem>();
            Assert.That(Store(system).Owned(session.UserId.UserId, "ApartmentShopExpensiveTest"), Is.False);
            Assert.That(Balance(), Is.EqualTo(15)); Assert.That(system.Number(session.UserId), Is.EqualTo(paidNumber));
            ((IDictionary<NetUserId, PlayerGameStatus>) entities.System<GameTicker>().PlayerGameStatuses)[session.UserId] = PlayerGameStatus.JoinedGame;
            body = entities.SpawnEntity("MobHuman", outside.GridCoords); pair.Server.PlayerMan.SetAttachedEntity(session, body);
            var spawned = new PlayerSpawnCompleteEvent(body, session, null, false, false, 1, outside.MapUid, HumanoidCharacterProfile.DefaultWithSpecies("Human"));
            entities.EventBus.RaiseEvent(EventSource.Local, spawned);
            paidGrid = system.Instances[session.UserId].Grid;
            Assert.That(system.Instances[session.UserId].Template.ID, Is.EqualTo("ApartmentShopPaidTest"));
            Assert.That(entities.GetComponent<TransformComponent>(body).GridUid, Is.EqualTo(paidGrid));
            Assert.That(entities.GetComponent<TransformComponent>(body).LocalPosition, Is.EqualTo(new Vector2(9.5f, 6.5f)));
            Show(system, session); Action(system, session, "select", "DLApartmentBasic");
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan; var session = pair.Server.PlayerMan.Sessions.Single(); var system = entities.System<ApartmentSystem>();
            Assert.That(entities.GetComponent<TransformComponent>(body).GridUid, Is.EqualTo(paidGrid), "Selection must not teleport the player.");
            Assert.That(system.Number(session.UserId), Is.Not.EqualTo(paidNumber));
            var newHome = system.PrepareElevatorApartment(session.UserId);
            Assert.That(newHome.Grid, Is.Not.EqualTo(paidGrid));
            Assert.That(entities.EntityExists(paidGrid), Is.True, "Old apartment must survive while occupied.");
            Assert.That(entities.GetComponent<TransformComponent>(newHome.Furniture[plantId]).LocalPosition, Is.EqualTo(moved));
            Assert.That(system.Leave(session.UserId, out _), Is.True);
            Assert.That(system.Enter(session, session.UserId, out var message), Is.True, message);
            Assert.That(entities.GetComponent<TransformComponent>(body).GridUid, Is.EqualTo(newHome.Grid));
            Assert.That(Balance(), Is.EqualTo(15));
            system.Leave(session.UserId, out _); pair.Server.PlayerMan.SetAttachedEntity(session, null); entities.DeleteEntity(body);
        });
        await pair.CleanReturnAsync();
    }
}

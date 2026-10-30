using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ApartmentShopSystem = Content.Client._DeepLagoon.Apartments.ApartmentShopSystem;
using Content.Server._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Apartments;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentCatalogueTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static IApartmentLayoutStore Store(ApartmentSystem system) =>
        (IApartmentLayoutStore) typeof(ApartmentSystem).GetProperty("Store", Private)!.GetValue(system)!;
    private static void Show(ApartmentSystem system, ICommonSession session) =>
        typeof(ApartmentSystem).GetMethod("PublishShop", Private)!.Invoke(system, new object[] { session, "" });
    private static void Select(ApartmentSystem system, ICommonSession session, string template)
    {
        var tokens = (Dictionary<NetUserId, Guid>) typeof(ApartmentSystem).GetField("_shopTokens", Private)!.GetValue(system)!;
        typeof(ApartmentSystem).GetMethod("ShopAction", Private)!.Invoke(system,
            new object[] { new ApartmentShopAction(tokens[session.UserId], "select", template), new EntitySessionEventArgs(session) });
    }
    private static ApartmentShopState ClientState(IEntityManager entities) =>
        (ApartmentShopState) typeof(ApartmentShopSystem).GetField("_state", Private)!.GetValue(entities.System<ApartmentShopSystem>())!;

    [Test]
    public async Task AllSixMapsLoadWithTheirPricesAndUsableElevatorCabins()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            var entities = pair.Server.EntMan;
            var system = entities.System<ApartmentSystem>();
            var elevator = entities.System<ApartmentElevatorSystem>();
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            Assert.That(pair.Server.CfgMan.GetCVar(ApartmentCVars.DefaultTemplate), Is.EqualTo("DLApartmentSmall"));
            typeof(ApartmentSystem).GetMethod("EnsureMaps", Private)!.Invoke(system, null);
            foreach (var (id, price) in new[] { ("DLApartmentSmall", 0), ("DLApartmentNormal", 550),
                ("DLApartmentSilhouette", 1230), ("DLApartmentScrap", 450), ("DLApartmentSnowCabin", 1050), ("DLApartmentStrip", 2100) })
            {
                TestContext.Progress.WriteLine("Loading apartment template: " + id);
                var template = prototypes.Index<ApartmentTemplatePrototype>(id);
                Assert.That(template.Price, Is.EqualTo(price), id);
                Assert.That(template.Available && !template.ExistingOnly, Is.True, id);
                var home = (ApartmentSystem.Instance) typeof(ApartmentSystem).GetMethod("CreateFresh", Private)!
                    .Invoke(system, new object[] { new NetUserId(Guid.NewGuid()), id, true })!;
                var panel = (EntityUid) typeof(ApartmentElevatorSystem).GetMethod("PanelOn", Private)!.Invoke(elevator, new object[] { home.Grid })!;
                var body = entities.SpawnEntity("MobHuman", new EntityCoordinates(home.Grid, new Vector2(template.Arrival.X + .5f, template.Arrival.Y + .5f)));
                Assert.That(typeof(ApartmentElevatorSystem).GetMethod("Inside", Private)!.Invoke(elevator, new object[] { body, panel }), Is.True, id);
                Assert.That(typeof(ApartmentElevatorSystem).GetMethod("CabinDoors", Private)!.Invoke(elevator, new object[] { panel }), Is.Not.Null, id);
                entities.DeleteEntity(body);
                entities.DeleteEntity(home.Grid);
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RetiredBasicIsHiddenAndCannotBeSelectedOrGrantedToNewOwners()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            var session = pair.Server.PlayerMan.Sessions.Single();
            var system = pair.Server.EntMan.System<ApartmentSystem>();
            Assert.That(system.GrantTemplate(session.UserId, "DLApartmentBasic", out var owned), Is.EqualTo("apartment_unavailable"));
            Assert.That(owned, Is.False);
            Show(system, session);
            Select(system, session, "DLApartmentBasic");
            Assert.That(Store(system).Owned(session.UserId.UserId, "DLApartmentBasic"), Is.False);
            Assert.That(Store(system).HasLayout(session.UserId.UserId, "DLApartmentBasic"), Is.False);
            Assert.That(system.PrepareElevatorApartment(session.UserId).Template.ID, Is.EqualTo("DLApartmentSmall"));
        });
        await pair.RunTicksSync(20);
        await pair.Client.WaitAssertion(() =>
        {
            var state = ClientState(pair.Client.EntMan);
            Assert.That(state.Templates.Any(p => p.Id == "DLApartmentBasic"), Is.False);
            Assert.That(state.Templates.Single(p => p.Id == "DLApartmentSmall").Owned, Is.True);
            typeof(ApartmentShopSystem).GetMethod("Close", Private)!.Invoke(pair.Client.EntMan.System<ApartmentShopSystem>(), null);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExistingBasicLayoutRemainsSelectedAndCanBeRestoredAfterSwitching()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            var session = pair.Server.PlayerMan.Sessions.Single();
            var system = pair.Server.EntMan.System<ApartmentSystem>();
            var basic = pair.Server.ResolveDependency<IPrototypeManager>().Index<ApartmentTemplatePrototype>("DLApartmentBasic");
            var saved = Store(system).Select(session.UserId.UserId, basic);
            saved.OxygenPercent = 35;
            saved.Revision++;
            Assert.That(Store(system).Save(session.UserId.UserId, saved, saved.Revision - 1), Is.True);
            Assert.That(Store(system).Owned(session.UserId.UserId, basic.ID), Is.False, "Old free apartments can have saves without purchase receipts");
            Assert.That(system.PrepareElevatorApartment(session.UserId).Template.ID, Is.EqualTo(basic.ID));
            Show(system, session);
        });
        await pair.RunTicksSync(20);
        await pair.Client.WaitAssertion(() =>
        {
            var state = ClientState(pair.Client.EntMan);
            var basic = state.Templates.Single(p => p.Id == "DLApartmentBasic");
            Assert.That(basic.Owned && basic.Selected, Is.True);
        });
        await pair.Server.WaitAssertion(() => Select(pair.Server.EntMan.System<ApartmentSystem>(), pair.Server.PlayerMan.Sessions.Single(), "DLApartmentSmall"));
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var session = pair.Server.PlayerMan.Sessions.Single();
            var system = pair.Server.EntMan.System<ApartmentSystem>();
            Assert.That(Store(system).Load(session.UserId.UserId)!.Template, Is.EqualTo("DLApartmentSmall"));
            Assert.That(Store(system).HasLayout(session.UserId.UserId, "DLApartmentBasic"), Is.True);
            Show(system, session);
            Select(system, session, "DLApartmentBasic");
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var owner = pair.Server.PlayerMan.Sessions.Single().UserId;
            var system = pair.Server.EntMan.System<ApartmentSystem>();
            Assert.That(Store(system).Load(owner.UserId)!.Template, Is.EqualTo("DLApartmentBasic"));
            Assert.That(Store(system).Load(owner.UserId)!.OxygenPercent, Is.EqualTo(35));
            Assert.That(system.PrepareElevatorApartment(owner).Template.ID, Is.EqualTo("DLApartmentBasic"));
        });
        await pair.Client.WaitAssertion(() => typeof(ApartmentShopSystem).GetMethod("Close", Private)!.Invoke(pair.Client.EntMan.System<ApartmentShopSystem>(), null));
        await pair.CleanReturnAsync();
    }
}
#nullable enable
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using System.Reflection;
using Content.Client._DeepLagoon.InteractionPanel;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Systems.Ghost.Controls.Roles;
using Content.Server.Ghost.Roles;
using Content.Server.Ghost.Roles.Components;
using Content.Shared.Ghost.Roles;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Server.GameStates;
using Robust.Shared.Player;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class GhostRolesTguiTests
{
    [Test]
    public async Task PreviewSubscriptionsAcceptOnlyOfferedRolesAndReleaseSessionOverrides()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            var ghost = entities.SpawnEntity("MobObserver", map.GridCoords);
            var actual = entities.SpawnEntity("MobMonkeyPunpun", map.GridCoords);
            var unrelated = entities.SpawnEntity("MobMouse", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, ghost);
            var eui = new Content.Server.Ghost.Roles.UI.GhostRolesEui();
            pair.Server.ResolveDependency<Content.Server.EUI.EuiManager>().OpenEui(eui, session);
            var id = entities.GetComponent<GhostRoleComponent>(actual).Identifier;
            var pvs = entities.System<PvsOverrideSystem>();
            var overrides = (Dictionary<ICommonSession, HashSet<EntityUid>>) typeof(PvsOverrideSystem)
                .GetField("SessionOverrides", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pvs)!;
            eui.HandleMessage(new GhostRolePreviewSubscription([id, uint.MaxValue]));
            Assert.That(overrides[session], Does.Contain(actual));
            Assert.That(overrides[session], Does.Not.Contain(unrelated));
            eui.HandleMessage(new GhostRolePreviewSubscription(new uint[GhostRolePreviewSubscription.MaxRoles + 1]));
            Assert.That(overrides[session], Does.Contain(actual), "Oversized subscriptions are rejected");
            eui.HandleMessage(new GhostRolePreviewSubscription([]));
            Assert.That(overrides.TryGetValue(session, out var remaining) && remaining.Contains(actual), Is.False);
            eui.HandleMessage(new GhostRolePreviewSubscription([id]));
            eui.Close();
            Assert.That(overrides.TryGetValue(session, out remaining) && remaining.Contains(actual), Is.False);
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            entities.DeleteEntity(ghost);
            entities.DeleteEntity(actual);
            entities.DeleteEntity(unrelated);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NativeThumbnailsUseTheActualEntityAndRejectInvalidLayouts()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.ResolveDependency<IEntityManager>();
            var actual = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
            var host = new GhostRoleTguiHost(new TguiPanel(inheritChatAppearance: false, showWindowControls: false));
            try
            {
                var net = entities.GetNetEntity(actual);
                var info = new GhostRoleInfo { Identifier = 7, PreviewEntity = net };
                host.SetRoles([info]);
                uint[] subscribed = [];
                host.SubscriptionChanged += ids => subscribed = ids;
                host.SetLayout("7,10,20,32,32;7,100,20,88,88", 500, 400);
                host.Measure(new Vector2(500, 400));
                host.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(500, 400)));
                var views = host.Children.OfType<InteractionPanelSpriteView>().ToArray();
                Assert.That(views.Length, Is.EqualTo(2));
                Assert.That(views.All(view => view.NetEnt == net), Is.True);
                // Resolve the network reference exactly as SpriteView does when the entity arrives.
                foreach (var view in views) view.SetEntity(actual);
                Assert.That(views.All(view => ReferenceEquals(view.Sprite, entities.GetComponent<SpriteComponent>(actual))), Is.True,
                    "No prototype reconstruction: body, equipment, colors and shader layers belong to the actual entity");
                Assert.That(views[0].Position, Is.EqualTo(new Vector2(10, 20)));
                Assert.That(views[1].Size, Is.EqualTo(new Vector2(88, 88)));
                // Browser CSS pixels and native UI units may differ with UI scaling.
                // Both axes must use the full viewport, regardless of the role list's content height.
                foreach (var scale in new[] { 0.75f, 1.25f, 1.5f })
                {
                    var size = new Vector2(500, 400) * scale;
                    host.Measure(size);
                    host.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
                    Assert.That(views[0].Position, Is.EqualTo(new Vector2(10, 20) * scale));
                    Assert.That(views[0].Size, Is.EqualTo(new Vector2(32, 32) * scale));
                    Assert.That(views[1].Position, Is.EqualTo(new Vector2(100, 20) * scale));
                    Assert.That(views[1].Size, Is.EqualTo(new Vector2(88, 88) * scale));
                }
                Assert.That(subscribed, Is.EqualTo(new uint[] { 7 }));
                foreach (var invalid in new[] { "99,0,0,32,32", "7,NaN,0,32,32", "7,0,0,1000,1000", "7,-1,0,32,32" })
                {
                    host.SetLayout(invalid, 500, 400);
                    Assert.That(host.Children.OfType<InteractionPanelSpriteView>().Count(), Is.EqualTo(2));
                }
                host.SetRoles([]);
                Assert.That(host.Children.OfType<InteractionPanelSpriteView>(), Is.Empty);
                Assert.That(subscribed, Is.Empty);
                Assert.That(entities.Deleted(actual), Is.False, "Closing previews must never delete the offered character");
            }
            finally
            {
#pragma warning disable CS0618
                host.Dispose();
#pragma warning restore CS0618
                entities.DeleteEntity(actual);
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SpawnerPreviewsUseTheirTargetAndDeleteOnlyTheirOwnDummy()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.ResolveDependency<IEntityManager>();
            var host = new GhostRoleTguiHost(new TguiPanel(inheritChatAppearance: false, showWindowControls: false));
            try
            {
                host.SetRoles([new GhostRoleInfo { Identifier = 1, PreviewPrototype = "MobMouse" }]);
                host.SetLayout("1,10,10,32,32", 500, 400);
                var dummy = host.Children.OfType<InteractionPanelSpriteView>().Single().Entity!.Value.Owner;
                Assert.That(entities.GetComponent<MetaDataComponent>(dummy).EntityPrototype!.ID, Is.EqualTo("MobMouse"));
                host.SetLayout("1,20,20,64,64", 500, 400);
                Assert.That(host.Children.OfType<InteractionPanelSpriteView>().Single().Entity!.Value.Owner, Is.EqualTo(dummy));
                host.SetRoles([new GhostRoleInfo { Identifier = 1, PreviewPrototype = "MobHumanDummy" }]);
                Assert.That(entities.Deleted(dummy), Is.True);
                dummy = host.Children.OfType<InteractionPanelSpriteView>().Single().Entity!.Value.Owner;
                Assert.That(entities.GetComponent<MetaDataComponent>(dummy).EntityPrototype!.ID, Is.EqualTo("MobHumanDummy"));
                host.ClearPreviews();
                Assert.That(entities.Deleted(dummy), Is.True);
            }
            finally
            {
#pragma warning disable CS0618
                host.Dispose();
#pragma warning restore CS0618
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ServerPublishesConcreteCharacterInsteadOfMarkerIcon()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.ResolveDependency<IEntityManager>();
            var actual = entities.SpawnEntity("MobMonkeyPunpun", map.GridCoords);
            var marker = entities.SpawnEntity("SpawnPointGhostRatKing", map.GridCoords);
            var system = entities.System<GhostRoleSystem>();
            var infos = system.GetGhostRolesInfo(null);
            var actualInfo = infos.Single(role => role.Identifier == entities.GetComponent<GhostRoleComponent>(actual).Identifier);
            var markerInfo = infos.Single(role => role.Identifier == entities.GetComponent<GhostRoleComponent>(marker).Identifier);
            Assert.That(actualInfo.PreviewEntity, Is.EqualTo(entities.GetNetEntity(actual)));
            Assert.That(actualInfo.PreviewPrototype, Is.Null);
            Assert.That(markerInfo.PreviewEntity, Is.Null);
            Assert.That(markerInfo.PreviewPrototype?.Id, Is.EqualTo("MobRatKing"));
            entities.DeleteEntity(actual);
            entities.DeleteEntity(marker);
        });
        await pair.CleanReturnAsync();
    }
}

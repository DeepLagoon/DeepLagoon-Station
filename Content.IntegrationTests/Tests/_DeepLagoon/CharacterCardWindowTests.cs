#nullable enable
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Client._DeepLagoon.CharacterInfo;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared.DetailExaminable;
using Content.Shared.Verbs;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Localization;
using Robust.Shared.Network;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class CharacterCardWindowTests
{
    [Test]
    public async Task ModelFillsItsPanelAndStaysAboveControlsWhenResized()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.ResolveDependency<IEntityManager>();
            var root = pair.Client.ResolveDependency<IUserInterfaceManager>().WindowRoot;
            var entity = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
            try
            {
                CharacterCardWindow.Open(entity, "Тест", "Описание", "OOC", 0, 1, 2, 0);
                var window = root.Children.OfType<CharacterCardWindow>().Single();
                var host = (Control)typeof(CharacterCardWindow).GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var sprite = host.Children.OfType<SpriteView>().Single();
                Assert.That(sprite.Stretch, Is.EqualTo(SpriteView.StretchMode.Fill));
                foreach (var hasHeadshot in new[] { false, true })
                foreach (var size in new[] { new Vector2(950, 700), new Vector2(640, 560) })
                {
                    host.GetType().GetField("HasHeadshot")!.SetValue(host, hasHeadshot);
                    host.Measure(size);
                    host.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
                    var height = Math.Clamp(size.Y - (hasHeadshot ? 426 : 98), 128, 256);
                    Assert.That(sprite.Position, Is.EqualTo(new Vector2(34, hasHeadshot ? 384 : 56)));
                    Assert.That(sprite.Size, Is.EqualTo(new Vector2(232, height - 24)));
                    Assert.That(sprite.Position.Y + sprite.Size.Y, Is.LessThan(size.Y - 42));
                }
                var panel = host.Children.OfType<TguiPanel>().Single();
                var action = (Action<string, string>)typeof(TguiPanel).GetField("OnAction", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
                action("char_right", "{}");
                Assert.That(sprite.OverrideDirection, Is.EqualTo(Direction.West));
                action("char_left", "{}");
                Assert.That(sprite.OverrideDirection, Is.EqualTo(Direction.South));
                action("close", "{}");
            }
            finally { entities.DeleteEntity(entity); }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RepeatedOpenReusesWindowAndClosedCardCanReopen()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.ResolveDependency<IEntityManager>();
            var root = pair.Client.ResolveDependency<IUserInterfaceManager>().WindowRoot;
            var entity = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
            try
            {
                for (var i = 0; i < 20; i++)
                    CharacterCardWindow.Open(entity, "Тестовый персонаж", "Описание", "OOC", 0, 1, 2, 0);
                var first = root.Children.OfType<CharacterCardWindow>().Single();
                first.Close();
                Assert.That(root.Children.OfType<CharacterCardWindow>(), Is.Empty);
                CharacterCardWindow.Open(entity, "Тестовый персонаж", "Описание", "OOC", 0, 1, 2, 0);
                var reopened = root.Children.OfType<CharacterCardWindow>().Single();
                Assert.That(reopened, Is.Not.SameAs(first));
                reopened.Close();
            }
            finally { entities.DeleteEntity(entity); }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PredictedExamineReplaysDoNotOpenCardsAndServerVerbOpensOnce()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var client = pair.Client;
        var server = pair.Server;
        NetEntity target = default;
        await server.WaitPost(() =>
        {
            var entities = server.ResolveDependency<IEntityManager>();
            entities.System<SharedMapSystem>().CreateMap(out var map);
            var entity = entities.SpawnEntity("MobHumanDummy", new MapCoordinates(0, 0, map));
            var details = entities.EnsureComponent<DetailExaminableComponent>(entity);
            details.CharacterCard = true;
            details.Content = "Описание";
            entities.Dirty(entity, details);
            server.PlayerMan.SetAttachedEntity(server.PlayerMan.Sessions.Single(), entity);
            target = entities.GetNetEntity(entity);
        });
        await pair.RunTicksSync(30);
        ExamineVerb? verb = null;
        await client.WaitAssertion(() =>
        {
            var entities = client.ResolveDependency<IEntityManager>();
            var entity = entities.GetEntity(target);
            verb = entities.System<Content.Client.Verbs.VerbSystem>().GetLocalVerbs(entity, entity, typeof(ExamineVerb))
                .Single(v => v.Text == client.ResolveDependency<ILocalizationManager>().GetString("detail-examinable-verb-text")) as ExamineVerb;
            Assert.That(verb, Is.Not.Null);
            // Reconciliation repeats the client-side delegate without a new user click.
            for (var i = 0; i < 20; i++) verb!.Act!.Invoke();
        });
        await pair.RunTicksSync(30);
        await client.WaitAssertion(() =>
        {
            var root = client.ResolveDependency<IUserInterfaceManager>().WindowRoot;
            Assert.That(root.Children.OfType<CharacterCardWindow>(), Is.Empty);
            client.ResolveDependency<IEntityManager>().RaisePredictiveEvent(new ExecuteVerbEvent(target, verb!));
        });
        await pair.RunTicksSync(30);
        await client.WaitAssertion(() => client.ResolveDependency<IUserInterfaceManager>().WindowRoot.Children
            .OfType<CharacterCardWindow>().Single().Close());
        await pair.RunTicksSync(100);
        await client.WaitAssertion(() => Assert.That(client.ResolveDependency<IUserInterfaceManager>().WindowRoot.Children
            .OfType<CharacterCardWindow>(), Is.Empty));
        // Detach while session data is still present, before pool shutdown clears it.
        await server.WaitPost(() => server.PlayerMan.SetAttachedEntity(server.PlayerMan.Sessions.Single(), null));
        await pair.CleanReturnAsync();
    }
}

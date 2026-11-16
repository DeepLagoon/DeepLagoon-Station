using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.Chat;
using Content.Client.UserInterface.Controls;
using Content.Client.UserInterface.Systems.Chat;
using Content.Shared._DeepLagoon.Chat;
using Content.Shared.Chat;
using Content.Shared.Mind;
using Content.Shared.Verbs;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class AntighostEmoteTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task OutOfRangeDraftCanBeRetriedAndEchoesOnceToSender(bool includeNearby)
    {
        const string draft = "checks the contents of their bag";
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        var server = pair.Server;
        var client = pair.Client;
        var player = pair.Player!;
        var recipient = await server.AddDummySession();
        var map = await pair.CreateTestMap();
        var near = map.GridCoords.Offset(new Vector2(1, 0));
        var far = map.GridCoords.Offset(new Vector2(AntighostEmote.Range + 1, 0));
        var ui = client.ResolveDependency<IUserInterfaceManager>();
        var chat = ui.GetUIController<ChatUIController>();
        EntityUid target = default;

        FancyWindow EmoteWindow() => ui.WindowRoot.Children.OfType<FancyWindow>()
            .Single(window => window.Title == Loc.GetString("dl-antighost-title"));

        int EchoCount() => chat.History.Count(entry => entry.Msg.Channel == ChatChannel.Emotes && entry.Msg.Message == draft);

        await client.WaitPost(() => client.ResolveDependency<IConfigurationManager>()
            .SetCVar(AntighostEmoteCVars.Audience, includeNearby ? 2 : 1));
        await server.WaitPost(() =>
        {
            var sender = server.EntMan.SpawnEntity("MobHuman", map.GridCoords);
            target = server.EntMan.SpawnEntity("MobHuman", near);
            var minds = server.System<SharedMindSystem>();
            minds.TransferTo(minds.CreateMind(player.UserId), sender);
            server.PlayerMan.SetAttachedEntity(recipient, target);
            var verb = server.System<SharedVerbSystem>().GetLocalVerbs(target, sender, typeof(AlternativeVerb))
                .Single(candidate => candidate.Text == Loc.GetString("dl-antighost-title"));
            verb.Act!();
            server.System<SharedTransformSystem>().SetCoordinates(target, far);
        });
        await pair.RunTicksSync(10);

        await client.WaitAssertion(() =>
        {
            var input = Descendants(EmoteWindow()).OfType<LineEdit>().Single();
            input.Text = draft;
            input.ForceSubmitText();
            Assert.That(input.Editable, Is.False);
        });
        await pair.RunTicksSync(10);

        await client.WaitAssertion(() =>
        {
            var controls = Descendants(EmoteWindow()).ToArray();
            var input = controls.OfType<LineEdit>().Single();
            Assert.That(input.Text, Is.EqualTo(draft));
            Assert.That(input.Editable, Is.True);
            Assert.That(controls.OfType<Label>().Any(label => label.Visible &&
                label.Text == Loc.GetString("dl-antighost-out-of-range")), Is.True);
            Assert.That(EchoCount(), Is.Zero);
        });

        await server.WaitPost(() => server.System<SharedTransformSystem>().SetCoordinates(target, near));
        await client.WaitPost(() => Descendants(EmoteWindow()).OfType<LineEdit>().Single().ForceSubmitText());
        await pair.RunTicksSync(10);
        await client.WaitAssertion(() =>
        {
            Assert.That(ui.WindowRoot.Children.OfType<FancyWindow>()
                .Any(window => window.Title == Loc.GetString("dl-antighost-title")), Is.False);
            Assert.That(EchoCount(), Is.EqualTo(1));
        });

        await pair.CleanReturnAsync();
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (var child in control.Children)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }
}

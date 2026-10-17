using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Numerics;
using Content.Client._DeepLagoon.Apartments;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Art;
using Content.Server._DeepLagoon.Art;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class InterfaceStyleTests
{
    private static IEnumerable<Control> Descendants(Control root) => root.Children.SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private static void Press(BaseButton button)
    {
        var callback = (Action<BaseButton.ButtonEventArgs>) typeof(BaseButton).GetField("OnPressed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(button)!;
        callback(new BaseButton.ButtonEventArgs(button, null!));
    }

    [Test]
    public async Task NativeFallbackKeepsTheChoiceAndRoutesActionsWithoutCreatingABrowser()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        TguiPanel panel = null;
        var actions = new List<string>();
        var typedFallbacks = 0;
        await pair.Client.WaitAssertion(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(WebUiCVars.InterfaceStyle, 1);
            var registry = pair.Client.ResolveDependency<WebViewLifetimeRegistry>();
            var before = registry.Active.Count;
            panel = new TguiPanel(inheritChatAppearance: false);
            panel.OnAction += (action, _) => actions.Add(action);
            panel.SetState("DeepLagoonDemo", "{\"count\":7}");
            panel.SwitchToNative();
            panel.SwitchToNative();
            Assert.That(panel.UsingNative, Is.True);
            Assert.That(panel.Web.BrowserActive, Is.False);
            Assert.That(registry.Active.Count, Is.EqualTo(before), "Fallback must not initialize a hidden CEF browser");
            Assert.That(cfg.GetCVar(WebUiCVars.InterfaceStyle), Is.EqualTo(1), "A local failure must not overwrite player preference");
            Press(Descendants(panel).OfType<Button>().Single(button => button.Text == "+1"));
            var typed = new TguiPanel();
            typed.SetNativeFallback(() => typedFallbacks++);
            typed.SwitchToNative(); typed.SwitchToNative();
            Assert.That(typedFallbacks, Is.EqualTo(1));
#pragma warning disable CS0618
            typed.Dispose();
#pragma warning restore CS0618
        });
        await pair.Client.WaitRunTicks(2);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(actions, Is.EqualTo(new[] { "increment" }));
            panel.SetState("DeepLagoonDemo", "{\"count\":8}");
            Assert.That(Descendants(panel).OfType<RichTextLabel>().Any(), Is.True);
#pragma warning disable CS0618
            panel.Dispose();
#pragma warning restore CS0618
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NativeApartmentDraftNeedsApplyAndBlocksAccidentalClose()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        TguiPanel panel = null;
        var actions = new List<(string Action, string Payload)>();
        await pair.Client.WaitAssertion(() =>
        {
            pair.Client.ResolveDependency<IConfigurationManager>().SetCVar(WebUiCVars.InterfaceStyle, 2);
            panel = new TguiPanel(inheritChatAppearance: false);
            panel.OnAction += (action, payload) => actions.Add((action, payload));
            panel.SetState("ApartmentEditor", new TguiData().Number("revision", 1).Array("layout", Array.Empty<TguiData>())
                .Array("catalog", new[] { new TguiData().String("id", "Chair").String("name", "Стул") }).ToString());
            panel.SwitchToNative();
            actions.Clear();
            Press(Descendants(panel).OfType<Button>().Single(button => button.Text == "Добавить мебель в черновик"));
        });
        await pair.Client.WaitRunTicks(2);
        await pair.Client.WaitAssertion(() =>
        {
            var editor = Descendants(panel).OfType<NativeApartmentEditor>().Single();
            Assert.That(editor.Dirty, Is.True);
            Assert.That(panel.CanCloseNative(), Is.False);
            Assert.That(actions.Any(action => action.Action == "apply"), Is.False);
            var preview = actions.Last(action => action.Action == "preview");
            Assert.That(ApartmentEditorSystem.TryParseLayout(preview.Payload, out var layout), Is.True);
            Assert.That(layout.Single().Furniture, Is.EqualTo("Chair"));
            Press(Descendants(panel).OfType<Button>().Single(button => button.Text == "Сбросить черновик"));
        });
        await pair.Client.WaitRunTicks(2);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(panel.CanCloseNative(), Is.True);
            Assert.That(Descendants(panel).OfType<NativeApartmentEditor>().Single().Dirty, Is.False);
#pragma warning disable CS0618
            panel.Dispose();
#pragma warning restore CS0618
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NativePaintAndGridUploadsUseTheExistingValidatedFormats()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        NativeArtEditor art = null;
        TguiPanel grid = null;
        var artMessages = new List<string>();
        var gridMessages = new List<(string Action, string Payload)>();
        await pair.Client.WaitAssertion(() =>
        {
            art = new NativeArtEditor((action, payload) => { if (action == "save") artMessages.Add(payload); });
            art.Update(NativeUiData.Parse(new TguiData().String("document", ArtLimits.EmptyDocument).Number("revision", 0).ToString()));
            var canvas = Descendants(art).OfType<NativePaintCanvas>().Single();
            var stroke = (Action<Vector2, bool, bool>) typeof(NativePaintCanvas).GetField("Stroke", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas)!;
            stroke(new Vector2(.4f, .4f), true, false);
            stroke(new Vector2(.6f, .6f), false, true);
            Assert.That(art.Dirty, Is.True);
            Assert.That(artMessages, Is.Empty, "Painting must not save automatically");
            Press(Descendants(art).OfType<Button>().Single(button => button.Text == "Сохранить"));

            grid = new TguiPanel(inheritChatAppearance: false);
            grid.OnAction += (action, payload) =>
            {
                gridMessages.Add((action, payload));
                if (action != "applyStart") return;
                var start = NativeUiData.Parse(payload);
                grid.Web.Send("update", new TguiData().Object("data", new TguiData().String("uploadId", start.String("id")).Number("uploadNext", 0)).ToString());
            };
            grid.SetState("GridEditor", new TguiData().String("token", Guid.NewGuid().ToString()).Array("items", Array.Empty<TguiData>()).ToString());
            grid.SwitchToNative();
            Press(Descendants(grid).OfType<Button>().Single(button => button.Text == "Разместить в черновике"));
        });
        await pair.Client.WaitRunTicks(2);
        string document = "";
        await pair.Client.WaitAssertion(() =>
        {
            document = string.Concat(artMessages.Select(message => NativeUiData.Parse(message).String("text")));
            Assert.That(document.Length, Is.GreaterThan(100));
            Assert.That(gridMessages, Is.Empty);
            Press(Descendants(grid).OfType<Button>().Single(button => button.Text == "Применить"));
        });
        await pair.Client.WaitRunTicks(3);
        ArtDocument.Baked baked = null;
        await pair.Server.WaitAssertion(() =>
        {
            baked = ArtDocument.Bake(document);
            Assert.That(baked.HasInk, Is.True, "Native PNG layers must pass the same authoritative baker as TGUI");
        });
        await pair.Client.WaitAssertion(() =>
        {
            art.Update(NativeUiData.Parse(new TguiData().String("document", baked.Json).Number("revision", 1).Number("result", 1).ToString()));
            Assert.That(art.Dirty, Is.False);
            var chunk = NativeUiData.Parse(gridMessages.Single(message => message.Action == "applyChunk").Payload);
            var item = chunk.Array("items").Single();
            Assert.That(item.String("id"), Is.EqualTo("t:0:0"));
            Assert.That(item.String("kind"), Is.EqualTo("tile"));
#pragma warning disable CS0618
            art.Dispose(); grid.Dispose();
#pragma warning restore CS0618
        });
        await pair.CleanReturnAsync();
    }
}

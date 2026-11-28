using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Client._DeepLagoon.Economy;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Economy;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class MarketplaceUiTests
{
    [Test]
    public async System.Threading.Tasks.Task EmbeddedMarketplaceKeepsPreviewsAndCommentsAndOffersNoFinancialTabsOrActions()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var config = pair.Client.CfgMan; var before = config.GetCVar(WebUiCVars.WindowStyles);
            config.SetCVar(WebUiCVars.WindowStyles, WindowInterfaceStyle.WithChoice(before, "SectorMarket", false));
            try
            {
                using var host = new Content.Client.UserInterface.Controls.FancyWindow();
                using var view = new TradeWindow(_ => {}, true);
                host.ContentsContainer.AddChild(view.Embed()); host.OpenCentered();
                var offer = new TradeOfferView(Guid.NewGuid(), false, false, "Seller", "Steel", "Item", 100, 1, 100, 0, 30, long.MaxValue) { Prototype = "SheetSteel" };
                view.Update(new() { Token = Guid.NewGuid(), Offers = [offer], Status = "ready", ReadOnly = true, CanComment = true,
                    CommentAuthor = "Card Name", CommentOffer = offer.Id, Comments = [new("Other Card", "Still available?", 1000)] });
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var native = (BoxContainer)typeof(TradeWindow).GetField("_native", flags)!.GetValue(view)!;
                Assert.That(All(native).OfType<RichTextLabel>().Any(l => l.Text == "Still available?"), Is.True);
                Assert.That(All(native).Any(c => c.Name == "TradeItemPreview"), Is.True);
                Assert.That(All(native).OfType<Button>().Any(b => b.VisibleInTree && b.Text is "Купить" or "Выставить выбранный предмет на продажу"), Is.False);
                var action = typeof(TradeWindow).GetMethod("OnWebAction", flags)!;
                foreach (var command in new[] { "publish", "basketAdd", "basketConfirm", "confirm", "claim" })
                    action.Invoke(view, [command, "{\"id\":\"" + offer.Id + "\"}"]);
                Assert.That(typeof(TradeWindow).GetField("_outgoing", flags)!.GetValue(view), Is.Null);
                action.Invoke(view, ["section", "{\"value\":\"create\"}"]);
                Assert.That(typeof(TradeWindow).GetField("_section", flags)!.GetValue(view), Is.EqualTo("catalog"));
                action.Invoke(view, ["commentText", "{\"value\":\"My comment\"}"]);
                action.Invoke(view, ["comment", "{}"]);
                var request = (TradeRequest)typeof(TradeWindow).GetField("_outgoing", flags)!.GetValue(view)!;
                Assert.That(request.Action, Is.EqualTo(TradeAction.Comment)); Assert.That(request.Offer, Is.EqualTo(offer.Id));
                Assert.That(request.Description, Is.EqualTo("My comment"));
                var panel = (TguiPanel)typeof(TradeWindow).GetField("_panel", flags)!.GetValue(view)!;
                panel.SwitchToTgui(); panel.SwitchToNative();
                Assert.That(typeof(TradeWindow).GetField("_outgoing", flags)!.GetValue(view), Is.SameAs(request));
            }
            finally { config.SetCVar(WebUiCVars.WindowStyles, before); }
        });
        await pair.CleanReturnAsync();
    }
    private static IEnumerable<Control> All(Control parent)
    {
        foreach (var child in parent.Children) { yield return child; foreach (var item in All(child)) yield return item; }
    }
}

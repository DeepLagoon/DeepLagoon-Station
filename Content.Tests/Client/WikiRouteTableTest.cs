using System;
using System.Collections.Generic;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.WebUI;
using NUnit.Framework;

namespace Content.Tests.Client;

[TestFixture]
public sealed class WikiRouteTableTest
{
    private static Dictionary<string, string> Defaults() => new()
    {
        ["default"] = "page:InGame",
        ["page:InGame"] = "/ru/in_game",
        ["page:Rules"] = "/ru/rules",
        ["guide:CoreRules"] = "page:Rules",
    };

    [Test]
    public void BookOverrideOutranksInheritedSourceAndGuide()
    {
        var routes = new WikiRouteTable(Defaults(), """
            # An individual book overrides its base and shared topic.
            entity:BaseGuidebook = /ru/books
            entity:BookSpaceLaw = page:Law
            page:Law = /ru/law
            guide:NF14 = /ru/frontier
            """);
        Assert.That(routes.Resolve(new[] { "entity:BookSpaceLaw", "entity:BaseGuidebook", "guide:NF14" }), Is.EqualTo("/ru/law"));
        Assert.That(routes.Resolve(new[] { "entity:BookSecurity", "entity:BaseGuidebook", "guide:NF14" }), Is.EqualTo("/ru/books"));
        Assert.That(routes.Resolve(new[] { "guide:NF14" }), Is.EqualTo("/ru/frontier"));
    }

    [Test]
    public void EmptyRouteDisablesWithoutFallingBackToDefault()
    {
        var routes = new WikiRouteTable(Defaults(), "entity:BookSpaceLaw =\npage:Rules =");
        Assert.That(routes.Resolve(new[] { "entity:BookSpaceLaw", "guide:CoreRules" }), Is.Empty);
        Assert.That(routes.Resolve(new[] { "guide:CoreRules" }), Is.Empty);
        Assert.That(routes.Resolve(new[] { "guide:Unknown" }), Is.EqualTo("/ru/in_game"));
    }

    [Test]
    public void CyclesAndUnknownPageAliasesDoNotProduceUrls()
    {
        var routes = new WikiRouteTable(Defaults(), "page:A = page:B\npage:B = page:A");
        Assert.That(routes.Resolve(new[] { "page:A" }), Is.Empty);
        Assert.That(WikiNavigationPolicy.IsPath(routes.Resolve(new[] { "none" }, "page:Missing")), Is.False);
    }

    [TestCase("rules:mandatory", "local:MonolithRuleset")]
    [TestCase("rules:mandatory", "/ru/server_rules")]
    [TestCase("job:TsfInfantry", "page:Rules")]
    [TestCase("vessel:Takeaway", "/ru/ships/takeaway")]
    [TestCase("company:None", "external:https://example.org/company")]
    [TestCase("activation:BookAtmosDistro", "local:Atmospherics")]
    [TestCase("link:Chemicals", "/ru/chemistry")]
    public void ConfigurableEntryPointsResolve(string source, string destination)
    {
        var routes = new WikiRouteTable(Defaults(), source + " = " + destination);
        Assert.That(routes.Resolve(new[] { source }), Is.EqualTo(destination == "page:Rules" ? "/ru/rules" : destination));
    }

    [TestCase("entity:BookSpaceLaw")]
    [TestCase(" = /ru/law")]
    [TestCase("entity:Book\tSpaceLaw = /ru/law")]
    public void MalformedConfigurationIsRejected(string text)
        => Assert.Throws<ArgumentException>(() => new WikiRouteTable(Defaults(), text));

    [Test]
    public void CallerOwnedDefaultsAreNotChanged()
    {
        var defaults = Defaults();
        _ = new WikiRouteTable(defaults, "default = /ru/changed");
        Assert.That(defaults["default"], Is.EqualTo("page:InGame"));
    }
}

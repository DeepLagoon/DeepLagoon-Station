using System.Collections.Generic;
using Content.Client._DeepLagoon.WebUI;
using NUnit.Framework;

namespace Content.Tests.Client;

[TestFixture]
public sealed class WikiNavigationPolicyTest
{
    private static readonly HashSet<string> Pages = new() { "/ru/rules" };

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void EmptyBrowserUrlsAreRejectedWithoutThrowing(string url)
    {
        var uriExceptions = 0;
        void OnException(object sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is System.UriFormatException) uriExceptions++;
        }
        System.AppDomain.CurrentDomain.FirstChanceException += OnException;
        try
        {
            Assert.That(WikiNavigationPolicy.TryUri(url, out _), Is.False);
            Assert.That(uriExceptions, Is.Zero);
        }
        finally
        {
            System.AppDomain.CurrentDomain.FirstChanceException -= OnException;
        }
    }

    [TestCase("https://wiki.deep-lagoon-ss14.ru/ru/rules", true)]
    [TestCase("https://wiki.deep-lagoon-ss14.ru/ru/in_game", true)]
    [TestCase("https://wiki.deep-lagoon-ss14.ru/ru/in_game/jobs/engineer#tools", true)]
    [TestCase("https://wiki.deep-lagoon-ss14.ru/ru/in_game_other", false)]
    [TestCase("https://wiki.deep-lagoon-ss14.ru/ru/home", false)]
    [TestCase("https://wiki.deep-lagoon-ss14.ru/ru/in_game?edit=1", false)]
    [TestCase("https://wiki.deep-lagoon-ss14.ru/ru/in_game/%2fadmin", false)]
    [TestCase("https://example.com/ru/in_game", false)]
    [TestCase("http://wiki.deep-lagoon-ss14.ru/ru/in_game", false)]
    public void NewArticlesAreAllowedOnlyInPublicGameTree(string url, bool expected)
        => Assert.That(WikiNavigationPolicy.IsPage(url, Pages), Is.EqualTo(expected));

    [TestCase("https://wiki.deep-lagoon-ss14.ru/graphql", true)]
    [TestCase("https://example.com/graphql", false)]
    [TestCase("https://wiki.deep-lagoon-ss14.ru/graphql?query=test", false)]
    public void TreeRequestsStayOnOwnEndpoint(string url, bool expected)
        => Assert.That(WikiNavigationPolicy.IsTreeRequest(url), Is.EqualTo(expected));

    [TestCase("https://wiki.deep-lagoon-ss14.ru/wiki-design-assets/logo.png", true)]
    [TestCase("https://wiki.deep-lagoon-ss14.ru/guidebook/in_game/assets/dl-beaker.png", true)]
    [TestCase("https://example.com/guidebook/in_game/assets/dl-beaker.png", false)]
    [TestCase("https://wiki.deep-lagoon-ss14.ru/guidebook/in_game/assets_other/image.png", false)]
    [TestCase("https://wiki.deep-lagoon-ss14.ru/graphql", false)]
    public void PublishedArticleAssetsStayOnWikiOrigin(string url, bool expected)
        => Assert.That(WikiNavigationPolicy.IsAsset(url), Is.EqualTo(expected));

    [TestCase("https://docs.example.org/en/manual/jobs", true)]
    [TestCase("https://docs.example.org/en/rules", true)]
    [TestCase("https://docs.example.org/en/manual_other", false)]
    [TestCase("https://wiki.deep-lagoon-ss14.ru/en/manual", false)]
    [TestCase("https://docs.example.org/en/manual?edit=1", false)]
    [TestCase("https://docs.example.org/en/manual/%2fadmin", false)]
    [TestCase("https://user@docs.example.org/en/manual", false)]
    public void ConfiguredOriginRootAndExplicitPagesRemainRestricted(string url, bool expected)
        => Assert.That(WikiNavigationPolicy.IsPage(url, new HashSet<string> { "/en/rules" },
            "https://docs.example.org", "/en/manual"), Is.EqualTo(expected));

    [TestCase("http://docs.example.org")]
    [TestCase("https://docs.example.org/path")]
    [TestCase("https://user@docs.example.org")]
    [TestCase("https://docs.example.org?edit=1")]
    [TestCase("https://docs.example.org:444")]
    public void InvalidConfiguredOriginsAreRejected(string origin)
        => Assert.That(WikiNavigationPolicy.IsOrigin(origin), Is.False);
}

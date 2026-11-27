using Content.Shared._DeepLagoon.Economy;
using NUnit.Framework;

namespace Content.Tests.Shared;

[TestFixture]
public sealed class TradeTextTests
{
    [TestCase("<b>Товар</b> <i>дешёвый</i>", "Товар дешёвый")]
    [TestCase("&lt;strong&gt;Товар&lt;/strong&gt; &amp; доставка", "Товар & доставка")]
    [TestCase("Цена < 100 и качество > 90", "Цена < 100 и качество > 90")]
    [TestCase("<p title='a > b'>Первый</p><p>Второй</p>", "Первый Второй")]
    [TestCase("Товар<script>alert(1)</script><!-- comment --><style>bad</style>", "Товар")]
    public void ListingsShowReadablePlainText(string source, string expected) => Assert.That(TradeText.Plain(source), Is.EqualTo(expected));
}

using Content.Shared.Construction.Prototypes;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Localization;

[TestFixture]
public sealed class RussianFallbackTest
{
    [Test]
    public async Task ConstructionRecipesResolveRussianNamesAndDescriptions()
    {
        await using var pair = await PoolManager.GetServerClient();

        await pair.Server.WaitAssertion(() => VerifyConstruction(
            pair.Server.ResolveDependency<IPrototypeManager>(),
            pair.Server.ResolveDependency<ILocalizationManager>()));
        await pair.Client.WaitAssertion(() => VerifyConstruction(
            pair.Client.ResolveDependency<IPrototypeManager>(),
            pair.Client.ResolveDependency<ILocalizationManager>()));

        await pair.CleanReturnAsync();
    }

    private static void VerifyConstruction(IPrototypeManager prototypes, ILocalizationManager loc)
    {
        var chair = prototypes.Index<ConstructionPrototype>("Chair");
        var frame = prototypes.Index<ConstructionPrototype>("DLArtFrameItem");

        Assert.Multiple(() =>
        {
            Assert.That(chair.Name, Is.EqualTo(loc.GetString("construction-recipe-Chair-name")));
            Assert.That(chair.Description, Is.EqualTo(loc.GetString("construction-recipe-Chair-description")));
            Assert.That(chair.Name, Does.Match("[А-Яа-яЁё]"));
            Assert.That(chair.Description, Does.Match("[А-Яа-яЁё]"));
            Assert.That(frame.Name, Is.EqualTo("деревянная рамка"));
        });
    }

    [Test]
    public async Task RussianMessagesUseEnglishFallbackOnClientAndServer()
    {
        await using var pair = await PoolManager.GetServerClient();

        await pair.Server.WaitAssertion(() => VerifyLocalization(pair.Server.ResolveDependency<ILocalizationManager>()));
        await pair.Client.WaitAssertion(() => VerifyLocalization(pair.Client.ResolveDependency<ILocalizationManager>()));

        await pair.CleanReturnAsync();
    }

    private static void VerifyLocalization(ILocalizationManager loc)
    {
        Assert.Multiple(() =>
        {
            Assert.That(loc.DefaultCulture?.Name, Is.EqualTo("ru-RU"));
            Assert.That(loc.GetString("dl-character-setup-characters"), Is.EqualTo("Персонажи"));
            Assert.That(loc.GetEntityData("GridMagnet").Name, Is.EqualTo("гридовый магнит"));
            Assert.That(loc.GetEntityData("CEActionZFlightUp").Name, Is.EqualTo("Взлететь выше"));
            Assert.That(loc.GetEntityData("CEActionZFlightUp").Desc, Does.Match("[А-Яа-яЁё]"));
            Assert.That(loc.GetEntityData("HeadSynth").Name, Is.EqualTo("голова синтетика"));
            Assert.That(loc.GetString("marking-DemomTail1-tail"), Is.EqualTo("Хвост"));
            Assert.That(loc.GetString("ghost-role-information-familiar-name"), Is.EqualTo("Фамильяр"));
            Assert.That(loc.GetString("es-coughing-interjection-1"), Is.EqualTo("кхр"));

            // Imported Russian messages use Russian numeric formatting.
            Assert.That(loc.GetString("zzzz-fmt-power-watts", ("divided", 1.5), ("places", 1)),
                Is.EqualTo("1,5 кВт"));

            // The pack's version requires an extra variable, so this message keeps the English fallback.
            // Exercise both English number formatting and grammar functions in that fallback bundle.
            Assert.That(loc.GetString("lathe-menu-material-amount-missing",
                    ("amount", 1.5), ("unit", "sheet"), ("material", "steel"), ("missingAmount", 2.5)),
                Is.EqualTo("1.5 sheets of steel ([color=red]2.5 sheets missing[/color])"));
        });
    }
}

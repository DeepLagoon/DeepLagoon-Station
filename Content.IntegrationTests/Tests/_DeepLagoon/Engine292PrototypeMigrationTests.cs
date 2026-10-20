using System.Collections.Generic;
using System.Linq;
using Content.Shared.FixedPoint;
using Content.Shared.Store;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class Engine292PrototypeMigrationTests
{
    [Test]
    public async Task NativeCurrencyKeysPreserveYamlAndRejectUnknownPrototypes()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var serialization = server.ResolveDependency<ISerializationManager>();

        await server.WaitAssertion(() =>
        {
            var currency = prototypes.EnumeratePrototypes<CurrencyPrototype>().First().ID;
            var valid = new MappingDataNode { { currency, "12.5" } };
            var invalid = new MappingDataNode { { "Engine292MissingCurrency", "12.5" } };

            Assert.That(serialization.ValidateNode<Dictionary<ProtoId<CurrencyPrototype>, FixedPoint2>>(valid)
                .GetErrors(), Is.Empty);
            Assert.That(serialization.ValidateNode<Dictionary<ProtoId<CurrencyPrototype>, FixedPoint2>>(invalid)
                .GetErrors(), Is.Not.Empty);

            var read = serialization.Read<Dictionary<ProtoId<CurrencyPrototype>, FixedPoint2>>(valid);
            Assert.That(read[(ProtoId<CurrencyPrototype>) currency], Is.EqualTo(FixedPoint2.New(12.5f)));
            var written = serialization.WriteValue(read);
            var roundTrip = serialization.Read<Dictionary<ProtoId<CurrencyPrototype>, FixedPoint2>>(written);
            Assert.That(roundTrip, Is.EquivalentTo(read));
        });

        await pair.CleanReturnAsync();
    }
}

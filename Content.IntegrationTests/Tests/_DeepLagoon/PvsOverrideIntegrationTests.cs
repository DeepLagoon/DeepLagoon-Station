using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Server.Materials;
using Content.Shared.Materials.OreSilo;
using Robust.Server.GameStates;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class PvsOverrideIntegrationTests
{
    [Test]
    public async Task OreSiloPreloadOnlyRegistersValidLinks()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var server = pair.Server;
            var entities = server.EntMan;
            var session = server.PlayerMan.Sessions.Single();
            var body = entities.SpawnEntity("MobHuman", map.GridCoords);
            server.PlayerMan.SetAttachedEntity(session, body);

            var silo = entities.SpawnEntity(null, map.GridCoords);
            var link = typeof(OreSiloClientComponent).GetField(nameof(OreSiloClientComponent.Silo))!;
            var broken = entities.EnsureComponent<OreSiloClientComponent>(entities.SpawnEntity(null, map.GridCoords));
            var working = entities.EnsureComponent<OreSiloClientComponent>(entities.SpawnEntity(null, map.GridCoords));
            link.SetValue(broken, EntityUid.Invalid);
            link.SetValue(working, silo);

            entities.System<OreSiloSystem>().Update(1f);

            var pvs = entities.System<PvsOverrideSystem>();
            var overrides = (Dictionary<ICommonSession, HashSet<EntityUid>>) typeof(PvsOverrideSystem)
                .GetField("SessionOverrides", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pvs)!;
            Assert.That(overrides[session], Does.Contain(silo));
            Assert.That(overrides[session], Does.Not.Contain(EntityUid.Invalid));
        });
        await pair.CleanReturnAsync();
    }
}

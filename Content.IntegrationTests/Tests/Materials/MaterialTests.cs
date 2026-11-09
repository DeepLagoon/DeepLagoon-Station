#nullable enable
using Content.Server.Destructible;
using Content.Server.Materials;
using Content.Server.Stack;
using Content.Shared.Stacks;
using Content.Shared.Materials;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Materials
{
    /// <summary>
    /// Materials and stacks have some odd relationships to entities,
    /// so we need some test coverage for them.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(StackSystem))]
    [TestOf(typeof(MaterialPrototype))]
    public sealed class MaterialPrototypeSpawnsStackMaterialTest
    {
        [Test]
        public async Task DestructionSpillsMaterialsAcrossTicksAfterGridDeletion()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
            var server = pair.Server;
            var em = server.EntMan;
            var testMap = await pair.CreateTestMap();
            var transform = server.System<SharedTransformSystem>();
            var materials = server.System<MaterialStorageSystem>();
            var destructible = server.System<DestructibleSystem>();
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            const string materialId = "Steel";
            var material = prototypes.Index<MaterialPrototype>(materialId);
            var prototype = material.StackEntity!.Value;
            var stackLimit = server.System<StackSystem>().GetMaxCount(prototype);
            Assert.That(prototypes.Index(prototype).TryComp<PhysicalCompositionComponent>(out var composition, em.ComponentFactory), Is.True);
            var perSheet = composition!.MaterialComposition[materialId];
            var sheets = stackLimit * 72 + 3;

            (int Stacks, int Sheets) CountSpill()
            {
                var stacks = 0;
                var total = 0;
                var query = em.EntityQueryEnumerator<StackComponent, MetaDataComponent, TransformComponent>();
                while (query.MoveNext(out _, out var stack, out var meta, out var xform))
                {
                    if (meta.EntityPrototype?.ID != prototype.Id || xform.MapID != testMap.MapId)
                        continue;

                    Assert.That(stack.Count, Is.InRange(1, stackLimit));
                    total += stack.Count;
                    stacks++;
                }

                return (stacks, total);
            }

            await server.WaitAssertion(() =>
            {
                var silo = em.SpawnEntity("MachineMaterialSilo", testMap.GridCoords);
                Assert.That(materials.TryChangeMaterialAmount(silo, materialId, sheets * perSheet, localOnly: true), Is.True);
                Assert.That(transform.GetMapCoordinates(silo).MapId, Is.EqualTo(testMap.MapId));
                destructible.DestroyEntity(silo);
                Assert.That(CountSpill(), Is.EqualTo((0, 0)));
                em.DeleteEntity(testMap.Grid.Owner);
            });

            await server.WaitRunTicks(1);
            await server.WaitAssertion(() => Assert.That(CountSpill().Stacks, Is.InRange(1, 64)));
            await server.WaitRunTicks(90);
            await server.WaitAssertion(() => Assert.That(CountSpill(), Is.EqualTo((73, sheets))));
            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task MaterialPrototypeSpawnsStackMaterial()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            await server.WaitIdleAsync();

            var mapSystem = server.System<SharedMapSystem>();
            var prototypeManager = server.ResolveDependency<IPrototypeManager>();
            var entityManager = server.ResolveDependency<IEntityManager>();

            var testMap = await pair.CreateTestMap();

            await server.WaitAssertion(() =>
            {
                var allMaterialProtos = prototypeManager.EnumeratePrototypes<MaterialPrototype>();
                var coords = testMap.GridCoords;

                Assert.Multiple(() =>
                {
                    foreach (var proto in allMaterialProtos)
                    {
                        if (proto.StackEntity == null)
                            continue;

                        var spawned = entityManager.SpawnEntity(proto.StackEntity, coords);

                        Assert.That(entityManager.TryGetComponent<StackComponent>(spawned, out var stack),
                            $"{proto.ID} 'stack entity' {proto.StackEntity} does not have the stack component");

                        Assert.That(entityManager.HasComponent<MaterialComponent>(spawned),
                            $"{proto.ID} 'material stack' {proto.StackEntity} does not have the material component");

                        StackPrototype? stackProto = null;
                        Assert.That(stack?.StackTypeId != null && prototypeManager.TryIndex(stack.StackTypeId, out stackProto),
                            $"{proto.ID} material has no stack prototype");

                        if (stackProto != null)
                            Assert.That(proto.StackEntity, Is.EqualTo(stackProto.Spawn));
                    }
                });

                mapSystem.DeleteMap(testMap.MapId);
            });

            await pair.CleanReturnAsync();
        }
    }
}

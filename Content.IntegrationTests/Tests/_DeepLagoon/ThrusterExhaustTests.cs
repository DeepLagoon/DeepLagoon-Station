using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Power.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared.Damage;
using Content.Shared.Light.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.SurveillanceCamera.Components;
using Content.Shared.Tag;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed partial class ThrusterExhaustTests
{
    [RegisterComponent]
    public sealed partial class ForwardedExhaustProbeComponent : Component;

    public sealed class ForwardedExhaustProbeSystem : EntitySystem
    {
        public EntityUid? Wall;
        public EntityUid? Decoration;
        public int ForwardedHits;

        public override void Initialize()
            => SubscribeLocalEvent<ForwardedExhaustProbeComponent, BeforeDamageChangedEvent>(OnDamage);

        private void OnDamage(EntityUid uid, ForwardedExhaustProbeComponent component, ref BeforeDamageChangedEvent args)
        {
            if (uid != Wall || Decoration is not { } target)
                return;

            ForwardedHits++;
            EntityManager.System<DamageableSystem>().TryChangeDamage(target, args.Damage, origin: args.Origin);
        }
    }

    [TestCase(0)]
    [TestCase(90)]
    [TestCase(180)]
    [TestCase(270)]
    public async Task ExhaustDamagesStructuresButPreservesGrillesAndFloor(int degrees)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var transforms = entities.System<SharedTransformSystem>();
            var tiles = new List<(Vector2i, Tile)>();
            for (var x = -4; x <= 4; x++)
            for (var y = -4; y <= 4; y++)
                tiles.Add((new Vector2i(x, y), map.Tile.Tile));
            maps.SetTiles(map.Grid, tiles);
            transforms.SetLocalRotation(map.Grid, Angle.FromDegrees(31));

            var rotation = Angle.FromDegrees(degrees);
            var position = new Vector2(0.5f, 0.5f);
            EntityCoordinates At(float x, float y) => new(map.Grid, position + rotation.RotateVec(new Vector2(x, y)));
            var source = entities.SpawnEntity("DebugThruster", At(0, 0));
            transforms.SetLocalRotation(source, rotation);
            var hit = new List<EntityUid>();
            foreach (var prototype in new[] { "WallSolid", "ComputerShuttle", "Thruster", "GasVolumePump", "CrateGenericSteel", "ThrusterUnanchored" })
                hit.Add(entities.SpawnEntity(prototype, At(0, 1)));

            var safe = new List<EntityUid>();
            foreach (var prototype in new[] { "Grille", "GrilleBroken", "GrilleDiagonal", "Catwalk", "CatwalkMono", "DarkCatwalkMono", "WhiteCatwalkMono",
                         "WallArmorAddonSolid", "WallArmorAddonSolidCornerL", "WallArmorAddonSolidCornerR",
                         "WallArmorAddonSolidBroken", "WallArmorAddonSolidBrokenCornerL", "WallArmorAddonSolidBrokenCornerR",
                         "Window", "WindowDirectional", "WindowDiagonal", "ReinforcedWindow", "PlasmaReinforcedWindowDirectional",
                         "UraniumReinforcedWindowDirectional", "PlastitaniumWindow", "N14Window", "GasPassiveVent",
                         "GreebleAntenna1", "GreebleAntenna2", "GreebleRadarpanel2", "GreebleRadiator1",
                         "SurveillanceCameraGeneral", "SurveillanceCameraTSF", "SurveillanceCameraConstructed",
                         "Poweredlight", "PoweredlightExterior", "PoweredStrobeRedEnabled", "PoweredLightPostSmall",
                         "AlwaysPoweredWallLight", "AlwaysPoweredStrobeLight", "LightBulb", "LightTube",
                         "Railing", "RailingCorner", "RailingCornerSmall", "RailingRound",
                         "SignElectricalMed", "SignScience", "SignDirectionalAtmos", "BarSign",
                         "GasPressurePump", "GasPressurePumpOnMax", "GasPressurePumpAlt1", "GasPressurePumpAlt2",
                         "GasPressureRegulator", "GasPressureRegulatorAlt1", "GasPressureRegulatorAlt2" })
                safe.Add(entities.SpawnEntity(prototype, At(0, 1)));
            safe.Add(entities.SpawnEntity("WallSolid", At(0, -1)));
            safe.Add(entities.SpawnEntity("WallSolid", At(2, 1)));

            // Contained machine parts must not be burned through their casing.
            var container = entities.System<SharedContainerSystem>().EnsureContainer<Container>(hit[4], "exhaust-test");
            var contained = entities.SpawnEntity("ThrusterUnanchored", At(0, 1));
            Assert.That(entities.System<SharedContainerSystem>().Insert(contained, container), Is.True);
            var sourceDamage = entities.GetComponent<DamageableComponent>(source).TotalDamage;

            Fire(entities, source);

            foreach (var target in hit)
            {
                if (entities.Deleted(target))
                    continue;
                Assert.That(entities.GetComponent<DamageableComponent>(target).TotalDamage.Float(), Is.GreaterThan(0),
                    entities.GetComponent<MetaDataComponent>(target).EntityPrototype!.ID);
            }
            foreach (var target in safe)
                Assert.That(entities.GetComponent<DamageableComponent>(target).TotalDamage.Float(), Is.EqualTo(0),
                    entities.GetComponent<MetaDataComponent>(target).EntityPrototype!.ID);
            Assert.That(entities.GetComponent<DamageableComponent>(source).TotalDamage, Is.EqualTo(sourceDamage));
            Assert.That(entities.GetComponent<DamageableComponent>(contained).TotalDamage.Float(), Is.EqualTo(0));
            foreach (var (indices, tile) in tiles)
                Assert.That(maps.GetTileRef(map.Grid, map.Grid.Comp, indices).Tile, Is.EqualTo(tile));

            var wall = hit[0];
            var before = entities.GetComponent<DamageableComponent>(wall).TotalDamage;
            var system = entities.System<ThrusterSystem>();
            system.DisableLinearThrusters(entities.GetComponent<ShuttleComponent>(map.Grid));
#pragma warning disable RA0002 // Fixture makes the next exhaust pulse due without changing production cooldowns.
            entities.GetComponent<ThrusterComponent>(source).NextFire = TimeSpan.Zero;
#pragma warning restore RA0002
            system.Update(0f);
            Assert.That(entities.GetComponent<DamageableComponent>(wall).TotalDamage, Is.EqualTo(before));
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("SmallThruster", 0f, 0.5f)]
    [TestCase("Thruster", 0f, 0.9f)]
    [TestCase("ThrusterLarge", 0.5f, 1.8f)]
    [TestCase("Thruster3x3", 1f, 2.7f)]
    public async Task LargerThrustersReachFurther(string prototype, float center, float range)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var tiles = new List<(Vector2i, Tile)>();
            for (var x = -2; x <= 4; x++)
            for (var y = -3; y <= 10; y++)
                tiles.Add((new Vector2i(x, y), map.Tile.Tile));
            maps.SetTiles(map.Grid, tiles);
            var source = entities.SpawnEntity(prototype, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            var near = entities.SpawnEntity("WallSolid", new EntityCoordinates(map.Grid, 0.5f + center, 0.5f + range));
            var far = entities.SpawnEntity("WallSolid", new EntityCoordinates(map.Grid, 0.5f + center, 2f + range));
            Fire(entities, source);
            Assert.That(entities.GetComponent<DamageableComponent>(near).TotalDamage.Float(), Is.GreaterThan(0));
            Assert.That(entities.GetComponent<DamageableComponent>(far).TotalDamage.Float(), Is.EqualTo(0));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ProtectedFamiliesInheritExhaustImmunityAcrossAllPrototypes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var factory = entities.ComponentFactory;
            bool DescendsFrom(EntityPrototype prototype, string root)
            {
                return prototypes.EnumerateAllParents<EntityPrototype>(prototype.ID, includeSelf: true)
                    .Any(parent => parent.id == root);
            }
            var checkedCount = 0;
            foreach (var prototype in prototypes.EnumeratePrototypes<EntityPrototype>())
            {
#pragma warning disable RA0002 // Read-only inspection of prototype tags; no live component is modified.
                var window = prototype.TryComp<TagComponent>(out var tags, factory) && tags.Tags.Contains("Window");
#pragma warning restore RA0002
                var light = prototype.TryComp<PoweredLightComponent>(out _, factory) ||
                    prototype.TryComp<LightBulbComponent>(out _, factory);
                var camera = prototype.TryComp<SurveillanceCameraComponent>(out _, factory) &&
                    prototype.TryComp<DamageableComponent>(out _, factory) &&
                    !prototype.TryComp<MobStateComponent>(out _, factory);
                if (!window && !light && !camera &&
                    !DescendsFrom(prototype, "BaseGreeble") && !DescendsFrom(prototype, "BaseRailing") &&
                    !DescendsFrom(prototype, "GasPassiveVent") &&
                    !DescendsFrom(prototype, "BaseSign") && !DescendsFrom(prototype, "BaseBarSign") &&
                    !DescendsFrom(prototype, "BrbSign") && !DescendsFrom(prototype, "WetFloorSign") &&
                    !DescendsFrom(prototype, "WoodenSign") && !DescendsFrom(prototype, "BitrunMapStructureFluffFokoffSign") &&
                    !DescendsFrom(prototype, "GasPressurePump") && !DescendsFrom(prototype, "GasPressureRegulator"))
                    continue;
                Assert.That(prototype.TryComp<ThrusterExhaustImmuneComponent>(out _, factory), Is.True, prototype.ID);
                checkedCount++;
            }
            Assert.That(checkedCount, Is.GreaterThan(0));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExhaustImmunityDoesNotBlockOrdinaryDamageFromTheSameOrigin()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            entities.System<SharedMapSystem>().SetTile(map.Grid, new Vector2i(0, 1), map.Tile.Tile);
            var source = entities.SpawnEntity("DebugThruster", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            var window = entities.SpawnEntity("ReinforcedWindow", new EntityCoordinates(map.Grid, 0.5f, 1.5f));
            var damage = entities.GetComponent<ThrusterComponent>(source).Damage;
            entities.System<DamageableSystem>().TryChangeDamage(window, damage, origin: source);
            var before = entities.GetComponent<DamageableComponent>(window).TotalDamage.Float();
            Assert.That(before, Is.GreaterThan(0));
            Fire(entities, source);
            Assert.That(entities.GetComponent<DamageableComponent>(window).TotalDamage.Float(), Is.EqualTo(before));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SupportWallCannotForwardExhaustDamageIntoProtectedDecoration()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var map = await pair.CreateTestMap();
        EntityUid source = default, wall = default, antenna = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            entities.System<SharedMapSystem>().SetTile(map.Grid, new Vector2i(0, 1), map.Tile.Tile);
            source = entities.SpawnEntity("DebugThruster", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            var position = new EntityCoordinates(map.Grid, 0.5f, 1.5f);
            wall = entities.SpawnEntity("WallSolid", position);
            entities.AddComponent<ForwardedExhaustProbeComponent>(wall);
            antenna = entities.SpawnEntity("GreebleAntenna1", position);
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var probe = entities.System<ForwardedExhaustProbeSystem>();
            probe.Wall = wall;
            probe.Decoration = antenna;
            probe.ForwardedHits = 0;
            try
            {
                Fire(entities, source);
                Assert.That(probe.ForwardedHits, Is.GreaterThan(0));
            }
            finally
            {
                probe.Wall = null;
                probe.Decoration = null;
            }
            Assert.That(entities.GetComponent<DamageableComponent>(antenna).TotalDamage.Float(), Is.Zero);
            Assert.That(entities.GetComponent<DamageableComponent>(wall).TotalDamage.Float(), Is.GreaterThan(0));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExhaustAlsoDamagesAnotherGridAndDoesNotComeFromGyroscopes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var otherGrid = maps.CreateGridEntity(map.MapId);
            maps.SetTile(otherGrid, new Vector2i(0, 1), map.Tile.Tile);
            var source = entities.SpawnEntity("DebugThruster", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            var wall = entities.SpawnEntity("WallSolid", new EntityCoordinates(otherGrid, 0.5f, 1.5f));
            Fire(entities, source);
            Assert.That(entities.GetComponent<DamageableComponent>(wall).TotalDamage.Float(), Is.GreaterThan(0));
            entities.System<ThrusterSystem>().DisableLinearThrusters(entities.GetComponent<ShuttleComponent>(map.Grid));
            var gyro = entities.SpawnEntity("DebugGyroscope", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            var system = entities.System<ThrusterSystem>();
            system.EnableThruster(gyro, entities.GetComponent<ThrusterComponent>(gyro));
            system.SetAngularThrust(entities.GetComponent<ShuttleComponent>(map.Grid), true);
            var before = entities.GetComponent<DamageableComponent>(wall).TotalDamage;
#pragma warning disable RA0002 // Fixture makes the next exhaust pulse due without changing production cooldowns.
            entities.GetComponent<ThrusterComponent>(gyro).NextFire = TimeSpan.Zero;
#pragma warning restore RA0002
            system.Update(0f);
            Assert.That(entities.GetComponent<DamageableComponent>(wall).TotalDamage, Is.EqualTo(before));
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("Plating")]
    [TestCase("FloorSteel")]
    public async Task SubfloorUtilitiesAreSafeWhileExposedMachineryStillBurns(string tile)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var map = await pair.CreateTestMap(true, tile);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            entities.System<SharedMapSystem>().SetTile(map.Grid, new Vector2i(0, 1), map.Tile.Tile);
            var source = entities.SpawnEntity("DebugThruster", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            var targetPosition = new EntityCoordinates(map.Grid, 0.5f, 1.5f);
            var pipe = entities.SpawnEntity("GasPipeStraight", targetPosition);
            var cable = entities.SpawnEntity("CableApcExtension", targetPosition);
            var vent = entities.SpawnEntity("GasVentPump", targetPosition);
            var distantWall = entities.SpawnEntity("WallSolid", new EntityCoordinates(map.Grid, 0.5f, 2.5f));
            Fire(entities, source);
            Assert.That(entities.GetComponent<DamageableComponent>(pipe).TotalDamage.Float(), Is.Zero);
            Assert.That(entities.GetComponent<DamageableComponent>(cable).TotalDamage.Float(), Is.Zero);
            Assert.That(entities.GetComponent<DamageableComponent>(vent).TotalDamage.Float(), Is.GreaterThan(0));
            Assert.That(entities.GetComponent<DamageableComponent>(distantWall).TotalDamage.Float(), Is.Zero,
                "The shorter plume must not touch the next wall through its fixture skin");
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("/SharedMaps/_Mono/Shuttles/Imported/Expedition/flashpoint.yml")]
    [TestCase("/SharedMaps/_Mono/Shuttles/Imported/Civilian/Devastator.yml")]
    [TestCase("/SharedMaps/_Mono/Shuttles/Imported/Expedition/ilmus.yml")]
    [TestCase("/SharedMaps/_Mono/Shuttles/Imported/Civilian/runo.yml")]
    [TestCase("/SharedMaps/_Mono/Shuttles/baeg.yml")]
    [TestCase("/SharedMaps/_Mono/Shuttles/USSP/sulak.yml")]
    public async Task StockShuttleTakesNoDamageFromAnyOfItsThrusters(string path)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        EntityUid grid = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            entities.System<SharedMapSystem>().CreateMap(out var mapId);
            Assert.That(entities.System<MapLoaderSystem>().TryLoadGrid(mapId,
                new ResPath(path), out var loaded), Is.True);
            grid = loaded!.Value.Owner;
        });
        await pair.RunTicksSync(1);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var system = entities.System<ThrusterSystem>();
            var engines = new List<EntityUid>();
            var before = new Dictionary<EntityUid, float>();
            foreach (var uid in entities.GetEntities().ToArray())
            {
                if (entities.GetComponent<TransformComponent>(uid).GridUid != grid)
                    continue;
                if (entities.TryGetComponent<DamageableComponent>(uid, out var damageable))
                    before[uid] = damageable.TotalDamage.Float();
                if (!entities.TryGetComponent<ThrusterComponent>(uid, out var thruster) ||
                    thruster.Type != ThrusterType.Linear || !thruster.Enabled)
                    continue;
                entities.GetComponent<ApcPowerReceiverComponent>(uid).Powered = true;
                Assert.That(system.CanEnable(uid, thruster), Is.True);
                system.EnableThruster(uid, thruster);
                engines.Add(uid);
            }
            Assert.That(engines, Is.Not.Empty);
            var shuttle = entities.GetComponent<ShuttleComponent>(grid);
            foreach (var direction in new[] { DirectionFlag.South, DirectionFlag.East, DirectionFlag.North, DirectionFlag.West })
                system.EnableLinearThrustDirection(shuttle, direction);
            foreach (var uid in engines)
                Assert.That(entities.GetComponent<ThrusterComponent>(uid).Firing, Is.True);
            for (var pulse = 0; pulse < 5; pulse++)
            {
                foreach (var uid in engines)
#pragma warning disable RA0002 // Fixture advances five exhaust pulses without waiting for real cooldowns.
                    entities.GetComponent<ThrusterComponent>(uid).NextFire = TimeSpan.Zero;
#pragma warning restore RA0002
                system.Update(0f);
            }
            foreach (var (uid, value) in before)
            {
                Assert.That(entities.Deleted(uid), Is.False, $"{path}: entity {uid} was destroyed by exhaust");
                Assert.That(entities.GetComponent<DamageableComponent>(uid).TotalDamage.Float(), Is.EqualTo(value),
                    entities.GetComponent<MetaDataComponent>(uid).EntityPrototype!.ID);
            }
            // Safety comes from layout/clearance, not a blanket immunity for this ship.
            var source = engines[0];
            var xform = entities.GetComponent<TransformComponent>(source);
            var probe = entities.SpawnEntity("WallSolid", new EntityCoordinates(grid,
                xform.LocalPosition + xform.LocalRotation.RotateVec(Vector2.UnitY)));
            Fire(entities, source);
            Assert.That(entities.GetComponent<DamageableComponent>(probe).TotalDamage.Float(), Is.GreaterThan(0));
        });
        await pair.CleanReturnAsync();
    }

    private static void Fire(IEntityManager entities, EntityUid source)
    {
        var system = entities.System<ThrusterSystem>();
        var component = entities.GetComponent<ThrusterComponent>(source);
        entities.GetComponent<ApcPowerReceiverComponent>(source).Powered = true;
        Assert.That(system.CanEnable(source, component), Is.True, "Floor tiles must not block the exhaust");
        system.EnableThruster(source, component);
        var transform = entities.GetComponent<TransformComponent>(source);
        var direction = (DirectionFlag) (1 << ((int) transform.LocalRotation.GetCardinalDir() / 2));
        system.EnableLinearThrustDirection(entities.GetComponent<ShuttleComponent>(transform.GridUid!.Value), direction);
#pragma warning disable RA0002 // Fixture makes the next exhaust pulse due without changing production cooldowns.
        component.NextFire = TimeSpan.Zero;
#pragma warning restore RA0002
        system.Update(0f);
    }
}

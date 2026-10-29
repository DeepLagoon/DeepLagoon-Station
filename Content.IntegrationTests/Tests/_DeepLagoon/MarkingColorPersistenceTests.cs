using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Content.Server.Station.Systems;
using Content.Shared.GameTicking;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using ClientAppearance = Content.Client.Humanoid.HumanoidAppearanceSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class MarkingColorPersistenceTests
{
    [Test, Explicit("Pass an appearance-only JSON snapshot in SS14_APPEARANCE_AUDIT_JSON; never commit player data.")]
    public async Task SavedAppearanceMatchesPreviewAfterStationSpawn()
    {
        using var document = JsonDocument.Parse(Environment.GetEnvironmentVariable("SS14_APPEARANCE_AUDIT_JSON")
            ?? throw new InvalidOperationException("SS14_APPEARANCE_AUDIT_JSON is required"));
        var data = document.RootElement;
        var appearance = new HumanoidCharacterAppearance(
            data.GetProperty("hair_name").GetString()!, Color.FromHex(data.GetProperty("hair_color").GetString()!),
            data.GetProperty("facial_hair_name").GetString()!, Color.FromHex(data.GetProperty("facial_hair_color").GetString()!),
            Color.FromHex(data.GetProperty("eye_color").GetString()!), Color.FromHex(data.GetProperty("skin_color").GetString()!),
            data.GetProperty("markings").EnumerateArray().Select(m => Marking.ParseFromDbString(m.GetString()!)!).ToList(),
            data.GetProperty("height").GetSingle(), data.GetProperty("width").GetSingle(),
            bodyFeatures: JsonSerializer.Deserialize<List<Content.Shared._DeepLagoon.BodyCustomization.BodyFeature>>(
                data.GetProperty("body_features_json").GetString()!), plantigradeLegs: data.GetProperty("plantigrade_legs").GetBoolean());
        var profile = HumanoidCharacterProfile.DefaultWithSpecies(data.GetProperty("species").GetString()!)
            .WithSex(Enum.Parse<Sex>(data.GetProperty("sex").GetString()!)).WithCharacterAppearance(appearance);
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var preview = new Dictionary<string, (Color Color, bool Visible, string State)>();
        Dictionary<string, (Color Color, bool Visible, string State)> Snapshot(IEntityManager entities, EntityUid uid)
        {
            var sprite = entities.GetComponent<SpriteComponent>(uid);
            var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(uid);
            var manager = IoCManager.Resolve<MarkingManager>();
            var result = new Dictionary<string, (Color Color, bool Visible, string State)>();
            foreach (var marking in humanoid.MarkingSet.Markings.Values.SelectMany(list => list))
            foreach (var spec in manager.Markings[marking.MarkingId].Sprites.OfType<SpriteSpecifier.Rsi>())
            {
                var key = $"{marking.MarkingId}-{spec.RsiState}";
                var layer = (SpriteComponent.Layer) sprite[sprite.LayerMapGet(key)];
                result.Add(key, (layer.Color, layer.Visible, layer.ActualState?.StateId.ToString() ?? ""));
            }
            foreach (var part in new[] { HumanoidVisualLayers.Head, HumanoidVisualLayers.Chest,
                         HumanoidVisualLayers.LArm, HumanoidVisualLayers.RArm, HumanoidVisualLayers.LHand, HumanoidVisualLayers.RHand,
                         HumanoidVisualLayers.LLeg, HumanoidVisualLayers.RLeg, HumanoidVisualLayers.LFoot, HumanoidVisualLayers.RFoot })
            {
                var layer = (SpriteComponent.Layer) sprite[sprite.LayerMapGet(part)];
                result.Add($"base/{part}", (layer.Color, layer.Visible, layer.ActualState?.StateId.ToString() ?? ""));
            }
            var order = string.Join(";", result.Keys.OrderBy(key => sprite.LayerMapGet(key.StartsWith("base/")
                ? Enum.Parse<HumanoidVisualLayers>(key[5..]) : (object) key)));
            result.Add("render-order", (Color.White, true, order));
            return result;
        }
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var dummy = entities.SpawnEntity(prototypes.Index<SpeciesPrototype>(profile.Species).DollPrototype, MapCoordinates.Nullspace);
            entities.System<ClientAppearance>().LoadProfile(dummy, profile);
            preview = Snapshot(entities, dummy);
            entities.DeleteEntity(dummy);
        });
        var map = await pair.CreateTestMap();
        NetEntity networked = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            var valid = (HumanoidCharacterProfile) profile.Validated(session, IoCManager.Instance!);
            Assert.That(valid.Appearance.Markings, Is.EqualTo(profile.Appearance.Markings), "Validation changed the saved selections");
            var mob = entities.System<StationSpawningSystem>().SpawnPlayerMob(map.GridCoords, null, valid, null);
            pair.Server.PlayerMan.SetAttachedEntity(session, mob);
            entities.EventBus.RaiseEvent(EventSource.Local,
                new PlayerSpawnCompleteEvent(mob, session, null, false, true, 1, map.MapUid, valid));
            var loaded = entities.GetComponent<HumanoidAppearanceComponent>(mob).MarkingSet.Markings.Values.SelectMany(list => list).ToArray();
            foreach (var marking in profile.Appearance.Markings)
                Assert.That(loaded.Single(m => m.MarkingId == marking.MarkingId).MarkingColors, Is.EqualTo(marking.MarkingColors), marking.MarkingId);
            networked = entities.GetNetEntity(mob);
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            var current = Snapshot(pair.Client.EntMan, pair.Client.EntMan.GetEntity(networked));
            Assert.That(current.Keys, Is.EquivalentTo(preview.Keys));
            foreach (var (key, expected) in preview)
                Assert.That(current[key], Is.EqualTo(expected), key);
            TestContext.Out.WriteLine($"Compared {current.Count} saved marking layers between preview and replicated character.");
        });
        foreach (var (itemId, slot) in new[] { ("ClothingUniformJumpsuitEngineering", "jumpsuit"), ("ClothingOuterHardsuitEngineering", "outerClothing") })
        {
            EntityUid item = default;
            await pair.Server.WaitAssertion(() =>
            {
                var entities = pair.Server.EntMan;
                var mob = entities.GetEntity(networked);
                item = entities.SpawnEntity(itemId, map.GridCoords);
                Assert.That(entities.System<InventorySystem>().TryEquip(mob, item, slot, force: true), Is.True);
            });
            await pair.RunTicksSync(10);
            await pair.Client.WaitAssertion(() =>
            {
                var current = Snapshot(pair.Client.EntMan, pair.Client.EntMan.GetEntity(networked));
                foreach (var (key, expected) in preview.Where(entry => !entry.Key.StartsWith("base/") && entry.Key != "render-order"))
                    if (current[key].Visible) Assert.That(current[key].Color, Is.EqualTo(expected.Color), $"{slot}/{key}");
            });
            await pair.Server.WaitAssertion(() =>
            {
                var entities = pair.Server.EntMan;
                Assert.That(entities.System<InventorySystem>().TryUnequip(entities.GetEntity(networked), slot, force: true, reparent: false), Is.True);
                entities.DeleteEntity(item);
            });
            await pair.RunTicksSync(10);
            await pair.Client.WaitAssertion(() =>
            {
                var current = Snapshot(pair.Client.EntMan, pair.Client.EntMan.GetEntity(networked));
                foreach (var (key, expected) in preview)
                    Assert.That(current[key], Is.EqualTo(expected), $"After removing {slot}/{key}");
            });
        }
        await pair.Server.WaitPost(() => pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null));
        await pair.CleanReturnAsync();
    }

    [TestCase(Sex.Male)]
    [TestCase(Sex.Female)]
    public async Task MechanicalArmsKeepEditorColorsAfterStationSpawnAndReplication(Sex sex)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        HumanoidCharacterProfile profile = null!;
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var lobby = ui.GetUIController<LobbyUIController>();
            lobby.OpenCharacterEditor();
            var editor = (HumanoidProfileEditor) typeof(LobbyUIController)
                .GetField("_profileEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lobby)!;
            editor.SetProfile(HumanoidCharacterProfile.DefaultWithSpecies("Synth").WithSex(sex), editor.CharacterSlot);
            Assert.That(editor.HandleTguiProfileAction("marking-add", "{\"value\":\"SynthMechanicalLArm\"}"), Is.True);
            var index = editor.Profile!.Appearance.Markings.FindIndex(m => m.MarkingId == "SynthMechanicalLArm");
            Assert.That(editor.HandleTguiProfileAction("marking-color", JsonSerializer.Serialize(new { index, layer = 0, value = "#171923" })), Is.True);
            profile = editor.Profile!;
            foreach (var marking in profile.Appearance.Markings.Where(m => m.MarkingId is "SynthMechanicalLArm" or "SynthMechanicalRArm"))
                Assert.That(marking.MarkingColors.Single(), Is.EqualTo(Color.FromHex("#171923")));
            ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single().Close();
        });
        var map = await pair.CreateTestMap();
        NetEntity networked = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            profile = (HumanoidCharacterProfile) profile.Validated(session, IoCManager.Instance!);
            var mob = entities.System<StationSpawningSystem>().SpawnPlayerMob(map.GridCoords, null, profile, null);
            pair.Server.PlayerMan.SetAttachedEntity(session, mob);
            entities.EventBus.RaiseEvent(EventSource.Local,
                new PlayerSpawnCompleteEvent(mob, session, null, false, true, 1, map.MapUid, profile));
            networked = entities.GetNetEntity(mob);
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var mob = entities.GetEntity(networked);
            var sprite = entities.GetComponent<SpriteComponent>(mob);
            var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(mob);
            foreach (var (id, state) in new[] { ("SynthMechanicalLArm", "l_arm"), ("SynthMechanicalRArm", "r_arm") })
            {
                var marking = humanoid.MarkingSet.Markings.Values.SelectMany(list => list).Single(m => m.MarkingId == id);
                Assert.That(marking.MarkingColors.Single(), Is.EqualTo(Color.FromHex("#171923")), id);
                Assert.That(sprite[sprite.LayerMapGet($"{id}-{state}")].Color, Is.EqualTo(Color.FromHex("#171923")), id);
            }
        });
        await pair.Server.WaitPost(() => pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EditableMarkingsKeepColorsThroughDatabaseValidationAndProfileLoading()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var profiles = new List<HumanoidCharacterProfile>();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var manager = pair.Server.ResolveDependency<MarkingManager>();
            var appearance = entities.System<SharedHumanoidAppearanceSystem>();
            var species = prototypes.EnumeratePrototypes<SpeciesPrototype>().Where(s => s.RoundStart).ToArray();
            var checkedIds = new HashSet<string>();
            var failures = new List<string>();
            foreach (var race in species.OrderBy(s => s.ID == "Synth" ? 0 : 1))
            {
                var mob = entities.SpawnEntity(race.Prototype, map.GridCoords);
                foreach (var sex in new[] { Sex.Male, Sex.Female })
                foreach (var prototype in prototypes.EnumeratePrototypes<MarkingPrototype>())
                {
                    if (prototype.EditorHidden || prototype.ForcedColoring || prototype.Sprites.Count == 0
                        || prototype.MarkingCategory is MarkingCategories.Hair or MarkingCategories.FacialHair
                        || !manager.CanBeApplied(race.ID, sex, prototype, prototypes)
                        || manager.MustMatchSkin(race.ID, prototype.BodyPart, out _, prototypes)
                        || race.ID != "Synth" && checkedIds.Contains(prototype.ID))
                        continue;

                    var marking = prototype.AsMarking();
                    for (var i = 0; i < marking.MarkingColors.Count; i++)
                        marking.SetColor(i, i % 2 == 0 ? Color.FromHex("#171923") : Color.FromHex("#843f67"));
                    marking.DrawOrder = 7;
                    var saved = Marking.ParseFromDbString(marking.ToString())!;
                    var profile = HumanoidCharacterProfile.DefaultWithSpecies(race.ID).WithSex(sex);
                    var validated = HumanoidCharacterAppearance.EnsureValid(profile.Appearance.WithMarkings(new() { saved }), race.ID, sex, profile.UnrestrictedMarkings);
                    profile = profile.WithCharacterAppearance(validated);
                    appearance.LoadProfile(mob, profile);
                    var loaded = entities.GetComponent<HumanoidAppearanceComponent>(mob).MarkingSet.Markings.Values
                        .SelectMany(list => list).Where(m => m.MarkingId == marking.MarkingId).ToArray();
                    if (loaded.Length != 1 || !loaded[0].MarkingColors.SequenceEqual(marking.MarkingColors))
                        failures.Add($"{race.ID}/{sex}/{marking.MarkingId}: {string.Join(";", loaded.Select(m => m.ToString()))}");
                    checkedIds.Add(prototype.ID);
                    profiles.Add(profile);
                }
                entities.DeleteEntity(mob);
            }
            TestContext.Out.WriteLine($"Audited {checkedIds.Count} editable marking prototypes in {profiles.Count} profiles.");
            Assert.That(failures, Is.Empty, string.Join('\n', failures));
            Assert.That(checkedIds, Does.Contain("SynthMechanicalLArm").And.Contain("SynthMechanicalRArm"));
        });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var appearance = entities.System<ClientAppearance>();
            var failures = new List<string>();
            foreach (var group in profiles.GroupBy(p => p.Species))
            {
                var dummy = entities.SpawnEntity(prototypes.Index<SpeciesPrototype>(group.Key).DollPrototype, MapCoordinates.Nullspace);
                foreach (var profile in group)
                {
                    appearance.LoadProfile(dummy, profile);
                    var expected = profile.Appearance.Markings.Single();
                    var prototype = prototypes.Index<MarkingPrototype>(expected.MarkingId);
                    var sprite = entities.GetComponent<SpriteComponent>(dummy);
                    for (var i = 0; i < prototype.Sprites.Count; i++)
                    {
                        if (prototype.Sprites[i] is not SpriteSpecifier.Rsi rsi)
                            continue;
                        var key = $"{prototype.ID}-{rsi.RsiState}";
                        if (!sprite.LayerMapTryGet(key, out var layer) || sprite[layer].Color != expected.MarkingColors[i])
                            failures.Add($"{profile.Species}/{profile.Sex}/{key}");
                    }
                }
                entities.DeleteEntity(dummy);
            }
            Assert.That(failures, Is.Empty, string.Join('\n', failures));
        });
        await pair.CleanReturnAsync();
    }
}

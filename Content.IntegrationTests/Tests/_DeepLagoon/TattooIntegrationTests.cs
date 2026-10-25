using System.Linq;
using Robust.Client.Utility;
using System.Reflection;
using System.Text.Json;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Content.Client._DeepLagoon.Tattoos;
using Content.Shared._DeepLagoon.Tattoos;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using NUnit.Framework;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Content.Server._DeepLagoon.Tattoos;
using Content.Shared.Humanoid.Markings;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class TattooIntegrationTests
{
    private static Tattoo Drawing() => new()
    {
        NormalBitmap = Content.Tests._DeepLagoon.TattooTests.Bitmap(),
        GlowBitmap = Content.Tests._DeepLagoon.TattooTests.Bitmap(true),
    };

    [Test]
    public async Task CharacterEditorMaximizeRestoresItsSizeAndPosition()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        CharacterEditorWindow window = null;
        HumanoidProfileEditor editor = null;
        await pair.Client.WaitPost(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var lobby = ui.GetUIController<LobbyUIController>(); lobby.OpenCharacterEditor();
            window = ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single();
            editor = (HumanoidProfileEditor)typeof(LobbyUIController).GetField("_profileEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lobby)!;
        });
        await pair.RunTicksSync(5);
        var size = System.Numerics.Vector2.Zero; var position = size;
        await pair.Client.WaitAssertion(() =>
        {
            size = window.Size; position = window.Position;
            Assert.That(editor.HandleTguiProfileAction("window-maximize", "{\"value\":true}"), Is.True);
            Assert.That(editor.HandleTguiProfileAction("window-maximize", "{\"value\":true}"), Is.True, "Repeated requests must be idempotent.");
        });
        await pair.RunTicksSync(5);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(window.Maximized, Is.True);
            Assert.That(window.Size, Is.EqualTo(window.Parent!.Size));
            Assert.That(window.Position, Is.EqualTo(System.Numerics.Vector2.Zero));
            using var state = JsonDocument.Parse(editor.CreateTguiProfileState("appearance").ToString());
            Assert.That(state.RootElement.GetProperty("maximized").GetBoolean(), Is.True);
            Assert.That(editor.HandleTguiProfileAction("window-maximize", "{\"value\":false}"), Is.True);
        });
        await pair.RunTicksSync(5);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(window.Maximized, Is.False);
            Assert.That(window.Size, Is.EqualTo(size));
            Assert.That(window.Position, Is.EqualTo(position));
            window.CloseConfirmed();
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SyntheticMarkingShellsPublishTattooAreasAndRenderBothInkChannels()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var uid = entities.SpawnEntity("MobSynthDummy", MapCoordinates.Nullspace);
            try
            {
                var profile = HumanoidCharacterProfile.DefaultWithSpecies("Synth");
                var tattoo = Drawing(); profile.Appearance.Tattoos.Add(tattoo);
                var appearance = entities.System<Content.Client.Humanoid.HumanoidAppearanceSystem>();
                appearance.LoadProfile(uid, profile);
                var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(uid);
                var sprite = entities.GetComponent<SpriteComponent>(uid);
                var data = new TattooEditorData();
                using var state = JsonDocument.Parse(data.Build(uid, profile.Appearance.Tattoos).ToString());
                var parts = state.RootElement.GetProperty("parts").EnumerateArray().ToArray();
                Assert.That(parts.Length, Is.EqualTo(Tattoo.BodyParts.Length));
                foreach (var part in parts)
                {
                    var frames = part.GetProperty("frames").EnumerateArray().ToArray();
                    Assert.That(frames.Length, Is.EqualTo(4));
                    var hasSkin = false;
                    foreach (var frame in frames)
                    {
                        using var mask = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(Convert.FromBase64String(frame.GetProperty("image").GetString()!.Split(',')[1]));
                        hasSkin |= mask.GetPixelSpan().ToArray().Any(pixel => pixel.A > 0);
                        Assert.That(frame.GetProperty("previewImage").GetString(), Does.StartWith("data:image/png;base64,"));
                        Assert.That(frame.GetProperty("placement").GetProperty("a").GetSingle(), Is.GreaterThan(0));
                    }
                    Assert.That(hasSkin, Is.True, $"{part.GetProperty("id").GetString()} must expose skin in at least one view; the far arm has an empty side frame.");
                }
                foreach (var glow in new[] { false, true })
                {
                    Assert.That(sprite.LayerMapTryGet($"dl-tattoo/{tattoo.Id}/{glow}", out var index), Is.True);
                    Assert.That(sprite[index].Visible, Is.True);
                    Assert.That(index, Is.GreaterThan(sprite.LayerMapGet("SynthHumanChest-torso_m")));
                }
                humanoid.PermanentlyHidden.Add(HumanoidVisualLayers.Chest);
                using var hidden = JsonDocument.Parse(data.Build(uid, profile.Appearance.Tattoos).ToString());
                Assert.That(hidden.RootElement.GetProperty("parts").EnumerateArray().Any(p => p.GetProperty("id").GetString() == "Chest"), Is.False);
            }
            finally { entities.DeleteEntity(uid); }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OrdinaryAndLuminousLayersStayBelowClothingAndFollowHiddenBodyParts()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var uid = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
            try
            {
                var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
                profile.Appearance.Markings.Add(new Marking("TattooHiveChest", new[] { Robust.Shared.Maths.Color.Red }));
                var tattoo = Drawing(); profile.Appearance.Tattoos.Add(tattoo);
                entities.System<Content.Client.Humanoid.HumanoidAppearanceSystem>().LoadProfile(uid, profile);
                var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(uid);
                var sprite = entities.GetComponent<SpriteComponent>(uid);
                var normalKey = "dl-tattoo/" + tattoo.Id + "/False";
                var glowKey = "dl-tattoo/" + tattoo.Id + "/True";
                Assert.That(sprite.LayerMapTryGet(normalKey, out var normal), Is.True);
                Assert.That(sprite.LayerMapTryGet(glowKey, out var glow), Is.True);
                Assert.That(sprite.LayerMapTryGet("jumpsuit", out var clothing), Is.True);
                Assert.That(normal, Is.LessThan(glow));
                Assert.That(glow, Is.LessThan(clothing));
                Assert.That(sprite.LayerMapTryGet("TattooHiveChest-tattoo_hive_chest", out var bodyMarking), Is.True);
                Assert.That(bodyMarking, Is.LessThan(normal), "Both kinds of ink must draw over existing markings.");
                profile.Appearance.Markings.Clear();
                profile.Appearance.Markings.Add(new Marking("TattooNightlingChest", new[] { Robust.Shared.Maths.Color.Blue }));
                entities.System<Content.Client.Humanoid.HumanoidAppearanceSystem>().LoadProfile(uid, profile);
                Assert.That(sprite.LayerMapTryGet(normalKey, out normal), Is.True);
                Assert.That(sprite.LayerMapTryGet(glowKey, out glow), Is.True);
                Assert.That(sprite.LayerMapTryGet("jumpsuit", out clothing), Is.True);
                Assert.That(sprite.LayerMapTryGet("TattooNightlingChest-tattoo_nightling", out var addedMarking), Is.True);
                Assert.That(addedMarking, Is.LessThan(normal), "Adding a marking must preserve the cached tattoo's draw order.");
                Assert.That(glow, Is.LessThan(clothing));
                Assert.That(((SpriteComponent.Layer) sprite[glow]).ShaderPrototype, Is.EqualTo("DeepLagoonTattooGlow"));
                foreach (var key in new[] { normalKey, glowKey })
                {
                    Assert.That(sprite.LayerMapTryGet(key + "/sampler-guard", out var guard), Is.True);
                    Assert.That(sprite.LayerMapTryGet(key, out var ink), Is.True);
                    Assert.That(guard, Is.EqualTo(ink - 1), "The sampler reset must draw immediately before its ink.");
                    var resetLayer = (SpriteComponent.Layer) sprite[guard];
                    var inkLayer = (SpriteComponent.Layer) sprite[ink];
                    Assert.That(resetLayer.ShaderPrototype, Is.EqualTo(inkLayer.ShaderPrototype));
                    Assert.That(resetLayer.Shader, Is.Not.SameAs(inkLayer.Shader));
                    Assert.That(resetLayer.CopyToShaderParameters, Is.Null, "The reset must issue a draw, not merely set parameters.");
                }
                Assert.That(sprite[normal].Visible && sprite[glow].Visible, Is.True);
                pair.Client.CfgMan.SetCVar(TattooCVars.Enabled, false);
                Assert.That(sprite[normal].Visible || sprite[glow].Visible, Is.False, "The viewer preference hides other people's ink immediately.");
                Assert.That(sprite[normal - 1].Visible || sprite[glow - 1].Visible, Is.False);
                pair.Client.CfgMan.SetCVar(TattooCVars.Enabled, true);
                Assert.That(sprite[normal].Visible && sprite[glow].Visible, Is.True);
                humanoid.TattoosEnabled = false;
                entities.System<TattooVisualizerSystem>().UpdateVisibility(uid, humanoid, sprite);
                Assert.That(sprite[normal].Visible || sprite[glow].Visible, Is.False, "The owner's opt-out hides ink from a viewer who opted in.");
                Assert.That(humanoid.Tattoos.Single().ContentEquals(tattoo), Is.True);
                humanoid.TattoosEnabled = true;
                entities.System<TattooVisualizerSystem>().UpdateVisibility(uid, humanoid, sprite);
                Assert.That(sprite[normal].Visible && sprite[glow].Visible, Is.True);
                humanoid.HiddenLayers[HumanoidVisualLayers.Chest] = SlotFlags.OUTERCLOTHING;
                entities.System<TattooVisualizerSystem>().UpdateVisibility(uid, humanoid, sprite);
                Assert.That(sprite[normal].Visible || sprite[glow].Visible, Is.False);
                Assert.That(sprite[normal - 1].Visible || sprite[glow - 1].Visible, Is.False);
                humanoid.HiddenLayers.Clear();
                entities.System<TattooVisualizerSystem>().UpdateVisibility(uid, humanoid, sprite);
                Assert.That(sprite[normal].Visible && sprite[glow].Visible, Is.True);
                humanoid.PermanentlyHidden.Add(HumanoidVisualLayers.Chest);
                entities.System<TattooVisualizerSystem>().UpdateVisibility(uid, humanoid, sprite);
                Assert.That(sprite[normal].Visible || sprite[glow].Visible, Is.False);
            }
            finally { entities.DeleteEntity(uid); }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task InkStaysAboveAnatomyAfterSizeVisibilityAndSkinChanges()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var uid = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
            try
            {
                var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
                var chestInk = Drawing();
                var armInk = Drawing(); armInk.BodyPart = HumanoidVisualLayers.LArm;
                profile.Appearance.Tattoos.AddRange([chestInk, armInk]);
                foreach (var part in new[] { "breasts", "belly", "butt" })
                {
                    var feature = Content.Shared._DeepLagoon.BodyCustomization.BodyCustomizationCatalog.Default(part);
                    feature.Enabled = true;
                    feature.Size = part == "breasts" ? "g" : "4";
                    profile.Appearance.BodyFeatures.Add(feature);
                }
                var appearance = entities.System<Content.Client.Humanoid.HumanoidAppearanceSystem>();
                var anatomy = entities.System<Content.Client._DeepLagoon.BodyCustomization.BodyCustomizationVisualizerSystem>();
                var sprite = entities.GetComponent<SpriteComponent>(uid);
                void CheckInk(int mode)
                {
                    foreach (var tattoo in new[] { chestInk, armInk })
                    foreach (var glow in new[] { false, true })
                    {
                        var key = $"dl-tattoo/{tattoo.Id}/{glow}";
                        Assert.That(sprite.LayerMapTryGet(key, out var index), Is.True);
                        Assert.That(index, Is.GreaterThan(anatomy.LastLayerIndex(uid, sprite)));
                        Assert.That(sprite.LayerMapTryGet(key + "/sampler-guard", out var guard), Is.True);
                        Assert.That(guard, Is.EqualTo(index - 1));
                    }
                    sprite.LayerMapTryGet($"dl-tattoo/{chestInk.Id}/True", out var chestLayer);
                    sprite.LayerMapTryGet(mode == 1 ? "jumpsuit" : "outerClothing", out var clothes);
                    Assert.That(mode == 1 ? chestLayer < clothes : chestLayer > clothes, Is.True);
                }
                foreach (var mode in new[] { 1, 2, 1 })
                {
                    foreach (var feature in profile.Appearance.BodyFeatures) feature.Visibility = mode;
                    appearance.LoadProfile(uid, profile);
                    CheckInk(mode);
                    sprite.LayerMapTryGet(HumanoidVisualLayers.LHand, out var hand);
                    sprite.LayerMapTryGet("gloves", out var gloves);
                    Assert.That(hand, Is.LessThan(gloves));
                    if (mode == 2) Assert.That(hand, Is.GreaterThan(anatomy.LastLayerIndex(uid, sprite)));
                    else { sprite.LayerMapTryGet("jumpsuit", out var uniform); Assert.That(hand, Is.GreaterThan(uniform)); }
                    var count = sprite.AllLayers.Count();
                    appearance.LoadProfile(uid, profile);
                    CheckInk(mode);
                    Assert.That(sprite.AllLayers.Count(), Is.EqualTo(count));
                    profile.Appearance.BodyFeatures.Single(f => f.Part == "belly").Size = "6";
                    appearance.LoadProfile(uid, profile);
                    CheckInk(mode);
                    appearance.SetSkinColor(uid, Robust.Shared.Maths.Color.White, verify: false);
                    CheckInk(mode);
                }
            }
            finally { entities.DeleteEntity(uid); }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MachineRequiresHeldToolOpenSkinAndCurrentRevision()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var artist = entities.SpawnEntity("MobHuman", map.GridCoords);
            var target = entities.SpawnEntity("MobHuman", map.GridCoords);
            var machine = entities.SpawnEntity("DLTattooMachine", map.GridCoords);
            var session = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.PlayerMan.SetAttachedEntity(session, artist);
            var hands = entities.System<SharedHandsSystem>();
            Assert.That(hands.TryPickupAnyHand(artist, machine, animate: false), Is.True);
            entities.EventBus.RaiseLocalEvent(machine, new AfterInteractEvent(artist, machine, target, map.GridCoords, true));
            var tool = entities.GetComponent<TattooMachineComponent>(machine);
            Assert.That(tool.Target, Is.EqualTo(target));
            var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(target);
            var drawing = Content.Tests._DeepLagoon.TattooTests.Drawing();
            void Apply(Tattoo tattoo, int revision, EntityUid actor) => entities.EventBus.RaiseLocalEvent(machine,
                new TattooMachineApplyMessage([tattoo], revision) { Actor = actor, UiKey = TattooMachineUiKey.Key });
            Apply(Drawing(), 1, artist);
            Assert.That(humanoid.Tattoos, Is.Empty, "Possessing the tool does not authorize arbitrary bitmap imports");
            Apply(drawing, 1, artist);
            Assert.That(humanoid.Tattoos.Single().ContentEquals(drawing), Is.True);
            var changed = drawing.Clone(); changed.X = .6f;
            humanoid.HiddenLayers[HumanoidVisualLayers.Chest] = SlotFlags.OUTERCLOTHING;
            Apply(changed, 2, artist);
            Assert.That(humanoid.Tattoos[0].X, Is.EqualTo(.5f));
            humanoid.HiddenLayers.Clear();
            Apply(changed, 2, artist);
            Assert.That(humanoid.Tattoos[0].X, Is.EqualTo(.6f));
            Apply(drawing, 2, artist);
            Assert.That(humanoid.Tattoos[0].X, Is.EqualTo(.6f), "A stale draft must not overwrite a newer drawing");
            Apply(drawing, 3, target);
            Assert.That(humanoid.Tattoos[0].X, Is.EqualTo(.6f), "Another actor cannot use this machine session");
            entities.DeleteEntity(machine);
            entities.DeleteEntity(target);
            entities.DeleteEntity(artist);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DisabledPreferenceReplicatesAndRejectsAlreadyOpenMachine()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        EntityUid artist = default, target = default, machine = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            artist = entities.SpawnEntity("MobHuman", map.GridCoords);
            target = entities.SpawnEntity("MobHuman", map.GridCoords);
            machine = entities.SpawnEntity("DLTattooMachine", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), artist);
            Assert.That(entities.System<SharedHandsSystem>().TryPickupAnyHand(artist, machine, animate: false), Is.True);
            entities.EventBus.RaiseLocalEvent(machine, new AfterInteractEvent(artist, machine, target, map.GridCoords, true));
            Assert.That(entities.GetComponent<TattooMachineComponent>(machine).Target, Is.EqualTo(target));
        });
        await pair.Client.WaitPost(() => pair.Client.CfgMan.SetCVar(TattooCVars.Enabled, false));
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<HumanoidAppearanceComponent>(artist).TattoosEnabled, Is.False);
            Assert.That(entities.System<TattooMachineSystem>().TattoosAllowed(artist), Is.False);
            entities.EventBus.RaiseLocalEvent(machine, new TattooMachineApplyMessage([Drawing()], 1) { Actor = artist, UiKey = TattooMachineUiKey.Key });
            Assert.That(entities.GetComponent<HumanoidAppearanceComponent>(target).Tattoos, Is.Empty);
            var interaction = new AfterInteractEvent(artist, machine, target, map.GridCoords, true);
            entities.EventBus.RaiseLocalEvent(machine, interaction);
            Assert.That(interaction.Handled, Is.False, "A disabled artist cannot reopen the machine.");
        });
        await pair.Client.WaitPost(() => pair.Client.CfgMan.SetCVar(TattooCVars.Enabled, true));
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<HumanoidAppearanceComponent>(artist).TattoosEnabled, Is.True);
            entities.GetComponent<HumanoidAppearanceComponent>(target).TattoosEnabled = false;
            var interaction = new AfterInteractEvent(artist, machine, target, map.GridCoords, true);
            entities.EventBus.RaiseLocalEvent(machine, interaction);
            Assert.That(interaction.Handled, Is.False, "An enabled artist cannot tattoo an opted-out target.");
            entities.GetComponent<HumanoidAppearanceComponent>(target).TattoosEnabled = true;
            interaction = new AfterInteractEvent(artist, machine, target, map.GridCoords, true);
            entities.EventBus.RaiseLocalEvent(machine, interaction);
            Assert.That(interaction.Handled, Is.True);
            entities.EventBus.RaiseLocalEvent(machine, new TattooMachineApplyMessage([Content.Tests._DeepLagoon.TattooTests.Drawing()], 2) { Actor = artist, UiKey = TattooMachineUiKey.Key });
            Assert.That(entities.GetComponent<HumanoidAppearanceComponent>(target).Tattoos, Has.Count.EqualTo(1));
            entities.DeleteEntity(machine); entities.DeleteEntity(target); entities.DeleteEntity(artist);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DrawingSurfaceIncludesTorsoAnatomyAndItsSecondaryColorOnlyInTorso()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var uid = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
            try
            {
                var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
                var appearance = entities.System<Content.Client.Humanoid.HumanoidAppearanceSystem>();
                var editor = new TattooEditorData();
                appearance.LoadProfile(uid, profile);
                string[] Frames(string part, string channel)
                {
                    using var state = JsonDocument.Parse(editor.Build(uid, profile.Appearance.Tattoos).ToString());
                    return state.RootElement.GetProperty("parts").EnumerateArray().Single(p => p.GetProperty("id").GetString() == part)
                        .GetProperty("frames").EnumerateArray().Select(f => f.GetProperty(channel).GetString()!).ToArray();
                }
                static int Pixels(string png)
                {
                    using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(Convert.FromBase64String(png.Split(',')[1]));
                    var count = 0;
                    for (var y = 0; y < image.Height; y++) for (var x = 0; x < image.Width; x++) if (image[x, y].A > 0) count++;
                    return count;
                }
                var baseMasks = Frames("Chest", "image");
                var head = Frames("Head", "previewImage");
                var belly = Content.Shared._DeepLagoon.BodyCustomization.BodyCustomizationCatalog.Default("belly");
                belly.Enabled = true; belly.Size = "6";
                var breasts = Content.Shared._DeepLagoon.BodyCustomization.BodyCustomizationCatalog.Default("breasts");
                breasts.Enabled = true; breasts.Size = "g"; breasts.SecondaryEnabled = true; breasts.SecondaryColor = "#FF0000";
                profile.Appearance.BodyFeatures.AddRange([belly, breasts]);
                appearance.LoadProfile(uid, profile);
                var expanded = Frames("Chest", "image");
                Assert.That(expanded.Where((png, direction) => Pixels(png) > Pixels(baseMasks[direction])), Is.Not.Empty,
                    "Anatomy outside the original torso must be part of the drawing contour.");
                Assert.That(Frames("Head", "previewImage"), Is.EqualTo(head));
                var previews = Frames("Chest", "previewImage");
                Assert.That(previews.All(p => p.StartsWith("data:image/png;base64,")), Is.True);
                var hasSecondary = false;
                foreach (var png in previews)
                {
                    using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(Convert.FromBase64String(png.Split(',')[1]));
                    for (var y = 0; y < image.Height; y++) for (var x = 0; x < image.Width; x++)
                        if (image[x, y].A > 0 && image[x, y].R > 50 && image[x, y].G == 0 && image[x, y].B == 0) hasSecondary = true;
                }
                Assert.That(hasSecondary, Is.True);
                profile.Appearance.BodyFeatures.Clear(); appearance.LoadProfile(uid, profile);
                Assert.That(Frames("Chest", "image"), Is.EqualTo(baseMasks));
            }
            finally { entities.DeleteEntity(uid); }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ProfileTguiDrawingSurvivesAppearanceEditsAndPublishesBodyMasks()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        HumanoidProfileEditor editor = null;
        CharacterEditorWindow window = null;
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var lobby = ui.GetUIController<LobbyUIController>();
            lobby.OpenCharacterEditor();
            window = ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single();
            editor = (HumanoidProfileEditor) typeof(LobbyUIController).GetField("_profileEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lobby)!;
            editor.SetProfile(HumanoidCharacterProfile.DefaultWithSpecies("Human"), editor.CharacterSlot);
            pair.Client.CfgMan.SetCVar(TattooCVars.Enabled, false);
            Assert.That(editor.HandleTguiProfileAction("tattoo-add", "{\"part\":\"Chest\",\"direction\":0}"), Is.False);
            pair.Client.CfgMan.SetCVar(TattooCVars.Enabled, true);
            Assert.That(editor.HandleTguiProfileAction("tattoo-add", "{\"part\":\"Chest\",\"direction\":0}"), Is.True);
            var tattoo = editor.Profile!.Appearance.Tattoos.Single();
            Assert.That(editor.HandleTguiProfileAction("tattoo-stroke", JsonSerializer.Serialize(new
            { id = tattoo.Id, points = ".4,.4;.6,.6", width = .02f, color = "#44FFAAFF", glow = true, erase = false })), Is.True);
            var baked = JsonSerializer.Serialize(new { id = tattoo.Id, recipe = "[{\"useBase\":true}]", x = .5f, y = .5f, scale = 1f, rotation = 0f, opacity = 1f });
            for (var offset = 0; offset < baked.Length; offset += 1000)
                Assert.That(editor.HandleTguiProfileAction("tattoo-bake-chunk", JsonSerializer.Serialize(new
                { id = tattoo.Id, index = offset / 1000, count = (baked.Length + 999) / 1000, text = baked.Substring(offset, System.Math.Min(1000, baked.Length - offset)) })), Is.True);
        });
        await pair.RunTicksSync(200);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(editor.Profile.Appearance.Tattoos.Single().ArtworkProof, Is.Not.Empty, "The game channel must return a server-authorized bitmap");
            Assert.That(editor.HandleTguiProfileAction("width", "{\"value\":0.95}"), Is.True);
            Assert.That(editor.Profile.Appearance.Tattoos.Single().Strokes, Is.Empty);
            Assert.That(editor.Profile.Appearance.Tattoos.Single().GlowBitmap, Is.Not.Empty);
            Assert.That(editor.IsDirty, Is.True);
            using var state = JsonDocument.Parse(editor.CreateTguiProfileState("markings").ToString());
            var frames = state.RootElement.GetProperty("tattooEditor").GetProperty("parts").EnumerateArray()
                .Single(p => p.GetProperty("id").GetString() == "Chest").GetProperty("frames").EnumerateArray().ToArray();
            Assert.That(frames.Length, Is.EqualTo(4));
            Assert.That(frames.All(f => f.GetProperty("image").GetString()!.StartsWith("data:image/png;base64,")), Is.True);
            Assert.That(frames.Select(f => f.GetProperty("image").GetString()).Distinct().Count(), Is.GreaterThan(1));
            Assert.That(frames.All(f => f.GetProperty("previewImage").GetString()!.StartsWith("data:image/png;base64,")), Is.True);
            var rawSkin = frames.Select(f => f.GetProperty("image").GetString()).ToArray();
            var beforeMarking = frames.Select(f => f.GetProperty("previewImage").GetString()).ToArray();
            Assert.That(editor.HandleTguiProfileAction("marking-add", "{\"value\":\"TattooHiveChest\"}"), Is.True);
            using var updated = JsonDocument.Parse(editor.CreateTguiProfileState("markings").ToString());
            var after = updated.RootElement.GetProperty("tattooEditor").GetProperty("parts").EnumerateArray()
                .Single(p => p.GetProperty("id").GetString() == "Chest").GetProperty("frames").EnumerateArray().ToArray();
            Assert.That(after.Select(f => f.GetProperty("image").GetString()), Is.EqualTo(rawSkin), "A marking does not change the clipping contour.");
            Assert.That(after.All(f => f.GetProperty("previewImage").GetString()!.StartsWith("data:image/png;base64,")), Is.True);
            Assert.That(after.Where((f, i) => f.GetProperty("previewImage").GetString() != beforeMarking[i]), Is.Not.Empty,
                "The real marking must be visible in its own direction behind the drawing surface.");
            var preserved = editor.Profile.Appearance.Tattoos.Single().Clone();
            pair.Client.CfgMan.SetCVar(TattooCVars.Enabled, false);
            Assert.That(editor.HandleTguiProfileAction("tattoo-clear", "{}"), Is.True);
            Assert.That(editor.Profile.Appearance.Tattoos, Is.Empty, "Clearing remains available even when new tattoos are disabled.");
            Assert.That(preserved.GlowBitmap, Is.Not.Empty);
            Assert.That(editor.IsDirty, Is.True);
            pair.Client.CfgMan.SetCVar(TattooCVars.Enabled, true);
            editor.ResetToDefault();
            window.CloseConfirmed();
        });
        await pair.CleanReturnAsync();
    }
}

using System.IO;
using System.Linq;
using Content.Client._Mono.Shipyard;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Client.UserInterface;
using System.Text.Json;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Mapping;

namespace Content.IntegrationTests.Tests._NF;

[TestFixture]
public sealed class ShipyardPreviewTests
{
    [Test]
    public async Task AllVesselsHaveReadablePlansWithoutSpawningEntities()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        await pair.Client.WaitAssertion(() =>
        {
            var client = pair.Client;
            var resources = client.ResolveDependency<IResourceManager>();
            var prototypes = client.ResolveDependency<IPrototypeManager>();
            var wall = new GridEditorItem { Kind = "entity", Prototype = "WallSolid", X = .5f, Y = .5f };
            var neighbour = new GridEditorItem { Kind = "entity", Prototype = "WallSolid", X = 1.5f, Y = .5f };
            var wallImages = new TguiWallImages(new TguiSpriteImages(), [wall, neighbour], prototypes);
            using var wallDocument = JsonDocument.Parse(new TguiData().Array("images", wallImages.Images(wall)).ToString());
            var corners = wallDocument.RootElement.GetProperty("images").EnumerateArray().ToArray();
            Assert.That(corners.Select(corner => corner.GetProperty("state").GetString()), Is.EqualTo(new[] { "solid1", "solid4", "solid0", "solid0" }));
            Assert.That(corners.Select(corner => corner.GetProperty("direction").GetInt32()), Is.EqualTo(new[] { 0, 2, 1, 3 }));
            Assert.That(corners.All(corner => corner.GetProperty("url").GetString()!.StartsWith("data:image/png;base64,")), Is.True);
            Assert.That(corners[2].GetProperty("url").GetString(), Is.Not.EqualTo(corners[3].GetProperty("url").GetString()));
            var before = client.EntMan.EntityQuery<MetaDataComponent>(true).Count();
            foreach (var vessel in prototypes.EnumeratePrototypes<VesselPrototype>().Where(vessel => !vessel.Abstract))
            {
                using var reader = resources.ContentFileReadText(vessel.ShuttlePath);
                var plan = ShipyardPreviewScene.Read(reader);
                Assert.That(plan.Items, Is.Not.Empty, vessel.ID);
                Assert.That(plan.Width, Is.Positive, vessel.ID);
                Assert.That(plan.Height, Is.Positive, vessel.ID);
            }
            var preview = client.System<ShipyardPreviewSystem>();
            var selected = prototypes.EnumeratePrototypes<VesselPrototype>().First(vessel => vessel.ID == "Meteor");
            using var selectedReader = resources.ContentFileReadText(selected.ShuttlePath);
            var data = preview.BuildData(selected, ShipyardPreviewScene.Read(selectedReader)).ToString();
            using var document = JsonDocument.Parse(data);
            Assert.That(document.RootElement.GetProperty("structures").GetArrayLength(), Is.Positive);
            Assert.That(document.RootElement.GetProperty("tiles").GetArrayLength(), Is.Positive);
            if (Environment.GetEnvironmentVariable("SHIPYARD_PREVIEW_EXPORT") is { Length: > 0 } output)
                File.WriteAllText(output, data);
            client.CfgMan.SetCVar(WebUiCVars.InterfaceStyle, 2);
            preview.Open(selected);
            Assert.That(preview.IsOpen, Is.True);
            Assert.That(client.ResolveDependency<IUserInterfaceManager>().WindowRoot.Children.OfType<ShipyardPreviewWindow>().Single().Panel.UsingNative, Is.False);
            Assert.That(client.EntMan.EntityQuery<MetaDataComponent>(true).Count(), Is.EqualTo(before));
            preview.Close();
            Assert.That(preview.IsOpen, Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public void PlanExcludesInventoryAndNeverReadsGameplayComponents()
    {
        using var reader = new StringReader("""
            meta: { format: 7, category: Grid }
            grids: [1]
            tilemap: {}
            entities:
            - proto: ""
              entities:
              - uid: 1
                components:
                - type: MapGrid
            - proto: FirelockGlass
              entities:
              - uid: 2
                components:
                - type: Transform
                  parent: 1
                  pos: -2.5,3.5
                  rot: 1.5707963267948966 rad
                - type: UnknownGameplayComponent
                  invalid: !type:DoesNotExist {}
              - uid: 3
                components:
                - type: Transform
                  parent: 2
                  pos: 0,0
            """);
        var plan = ShipyardPreviewScene.Read(reader);
        Assert.That(plan.Items.Count, Is.EqualTo(1));
        Assert.That(plan.Items[0].X, Is.EqualTo(-2.5f));
        Assert.That(plan.Items[0].Rotation, Is.EqualTo(90).Within(.001));
    }
}

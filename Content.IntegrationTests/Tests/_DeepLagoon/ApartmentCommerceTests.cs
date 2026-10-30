#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Content.Client._DeepLagoon.Apartments;
using Content.Client._DeepLagoon.WebUI;
using Content.Server._DeepLagoon.Apartments;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Apartments;
using Robust.Shared.Asynchronous;
using Robust.Shared.Player;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentCommerceTests
{
    [Test]
    public async Task EditorPurchasesArePersistentReplaySafeAndNeverReplaceTheDraft()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        await pair.Server.WaitPost(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            // Keep the legacy map geometry required by this furniture fixture.
            pair.Server.CfgMan.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentBasic");
            pair.Server.ResolveDependency<Robust.Shared.Prototypes.IPrototypeManager>().Index<ApartmentTemplatePrototype>("DLApartmentBasic").ExistingOnly = false;
        });
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        Guid token = default, order = Guid.NewGuid();
        string data = "";
        void Buy(string product, int quantity, long unit, Guid? key = null)
        {
            var editor = pair.Client.EntMan.System<ApartmentEditorSystem>();
            typeof(ApartmentEditorSystem).GetMethod("OnAction", flags)!.Invoke(editor, new object[] { "buyFurniture",
                JsonSerializer.Serialize(new { product, quantity, unitPrice = unit, order = key ?? order }) });
        }
        void ReadClient()
        {
            var editor = pair.Client.EntMan.System<ApartmentEditorSystem>();
            var window = (ApartmentEditorWindow) typeof(ApartmentEditorSystem).GetField("_window", flags)!.GetValue(editor)!;
            data = (string) typeof(TguiPanel).GetField("_data", flags)!.GetValue(window.Panel)!;
        }
        LagoonEconomySnapshot Account()
        {
            var session = pair.Server.PlayerMan.Sessions.Single();
            var task = pair.Server.ResolveDependency<IServerDbManager>().GetEconomyAsync(session.UserId, 0);
            pair.Server.ResolveDependency<ITaskManager>().BlockWaitOnTask(task);
#pragma warning disable RA0004
            return task.Result;
#pragma warning restore RA0004
        }
        await pair.Server.WaitAssertion(() =>
        {
            var session = pair.Server.PlayerMan.Sessions.Single();
            var coins = pair.Server.EntMan.System<Content.Server._DeepLagoon.Currency.LagoonCoinSystem>();
            pair.Server.ResolveDependency<ITaskManager>().BlockWaitOnTask(coins.Grant(session.UserId, 500, "furnishing-integration", null));
            typeof(ApartmentSystem).GetMethod("OpenLobbyEditor", flags)!.Invoke(pair.Server.EntMan.System<ApartmentSystem>(), new object[] { session });
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            ReadClient(); using var json = JsonDocument.Parse(data);
            var state = json.RootElement;
            Assert.That(state.GetProperty("commerceReady").GetBoolean(), Is.True);
            Assert.That(state.GetProperty("balance").GetInt64(), Is.EqualTo(500));
            token = state.GetProperty("token").GetGuid();
            var products = state.GetProperty("products").EnumerateArray().ToArray();
            Assert.That(products.Select(item => item.GetProperty("id").GetString()), Is.Unique);
            Assert.That(products.Single(item => item.GetProperty("id").GetString() == "DLAptSofa").GetProperty("variants").GetArrayLength(), Is.EqualTo(4));
            Buy("DLAptDecor006", 2, 35);
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var account = Account(); Assert.That(account.Balance, Is.EqualTo(430)); Assert.That(account.State.FurnitureStock["DLAptDecor006"], Is.EqualTo(12));
        });
        await pair.Client.WaitAssertion(() =>
        {
            var editor = pair.Client.EntMan.System<ApartmentEditorSystem>();
            var draft = editor.Draft.Select(item => item.Copy()).ToList();
            draft.Add(new ApartmentPlacement { Id = "local-draft", Furniture = "DLAptDecor006", X = 1, Y = 1 });
            typeof(ApartmentEditorSystem).GetMethod("OnAction", flags)!.Invoke(editor, new object[] { "preview", JsonSerializer.Serialize(new { layout = draft.Select(item => new { id=item.Id, furniture=item.Furniture, x=item.X, y=item.Y, rotation=item.Rotation }) }) });
            Buy("DLAptDecor006", 2, 35);
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() => Assert.That(pair.Client.EntMan.System<ApartmentEditorSystem>().Draft.Any(item => item.Id == "local-draft"), Is.True));
        await pair.Server.WaitAssertion(() => { Assert.That(Account().Balance, Is.EqualTo(430)); Assert.That(Account().State.FurnitureStock["DLAptDecor006"], Is.EqualTo(12)); });
        await pair.Client.WaitAssertion(() => Buy("DLAptDecor006", 1, 35));
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() => Assert.That(Account().Balance, Is.EqualTo(430), "Changing an existing receipt cannot charge again"));
        await pair.Client.WaitAssertion(() => Buy("DLAptDecor019", 1, 95, Guid.NewGuid()));
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() => Assert.That(Account().Balance, Is.EqualTo(430), "Individual sofa sections must not be sold"));
        await pair.Client.WaitAssertion(() => Buy("DLAptDecor018", 1, 95, Guid.NewGuid()));
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var account = Account(); Assert.That(account.Balance, Is.EqualTo(335)); Assert.That(account.State.FurnitureStock["DLAptDecor018"], Is.EqualTo(8));
            var system = pair.Server.EntMan.System<ApartmentSystem>(); var session = pair.Server.PlayerMan.Sessions.Single();
            var instance = system.Instances[session.UserId];
            var layout = (List<ApartmentPlacement>) typeof(ApartmentSystem).GetMethod("CurrentLayout", flags)!.Invoke(system, new object[] { instance })!;
            var scene = (ApartmentRoomScene) typeof(ApartmentSystem).GetMethod("CaptureRoom", flags)!.Invoke(system, new object[] { instance })!;
            var free = scene.Tiles.Select(item => new Vector2i((int)item.X, (int)item.Y)).Where(cell => !scene.Occupied.Contains(cell) &&
                !instance.Template.ReservedCells.Contains(cell) && cell != instance.Template.Arrival && !layout.Any(p => p.X==cell.X && p.Y==cell.Y)).Take(15).ToArray();
            Assert.That(free.Length, Is.GreaterThanOrEqualTo(15));
            for(var i=0;i<6;i++) layout.Add(new() { Id="bought-chair-"+i, Furniture="DLAptDecor006", X=free[i].X, Y=free[i].Y });
            Assert.That(system.Apply(session, Guid.NewGuid(), instance.Delta.Revision, layout), Does.Contain("Нет доступа"));
            Assert.That(system.Apply(session, token, instance.Delta.Revision, layout), Is.Null, "Purchased stock must work above the old prototype allowance");
            var overloaded = layout.Select(p=>p.Copy()).ToList();
            for(var i=0;i<9;i++) overloaded.Add(new() { Id="sofa-piece-"+i, Furniture=i%2==0?"DLAptDecor019":"DLAptDecor020", X=free[6+i].X, Y=free[6+i].Y });
            // The apply rate guard is independent of stock; validate the same authoritative pool directly.
            Assert.That(ApartmentLayout.Validate(instance.Template, overloaded,
                id => (ApartmentFurniturePrototype?) typeof(ApartmentSystem).GetMethod("Catalog", flags, new[] { typeof(ApartmentSystem.Instance), typeof(string) })!.Invoke(system, new object[] { instance, id }),
                scene, instance.MapFurniture.Values.Select(item=>item.Baseline), stock: id=>id=="DLAptDecor018"?8:400), Does.Contain("экземпляров"));
            var fresh = (ApartmentSystem.Instance) typeof(ApartmentSystem).GetMethod("CreateFresh", flags)!.Invoke(system, new object[] { session.UserId, instance.Template.ID, false })!;
            Assert.That(fresh.Furniture.Keys.Count(id=>id.StartsWith("bought-chair-")), Is.EqualTo(6), "Saved furniture must reload without depending on a warmed wallet cache");
            pair.Server.EntMan.DeleteEntity(fresh.Grid);
            typeof(ApartmentSystem).GetMethod("OpenLobbyEditor", flags)!.Invoke(system, new object[] { session });
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            ReadClient();
            using var document = JsonDocument.Parse(data);
            Assert.That(document.RootElement.GetProperty("commerceReady").GetBoolean(), Is.True);
            var output = Environment.GetEnvironmentVariable("DEEPLAGOON_APARTMENT_CAPTURE");
            if (!string.IsNullOrEmpty(output)) File.WriteAllText(output, data);
            var editor = pair.Client.EntMan.System<ApartmentEditorSystem>();
            var originalCount = editor.Draft.Count;
            var upload = Guid.NewGuid();
            var large = Enumerable.Range(0, 400).Select(i => new { id="upload-"+i, furniture="DLAptDecor006", x=3, y=3, rotation=0 }).ToArray();
            void Chunk(int index) => typeof(ApartmentEditorSystem).GetMethod("OnAction", flags)!.Invoke(editor, new object[] { "layoutChunk",
                JsonSerializer.Serialize(new { upload, operation="preview", index, count=16, layout=large.Skip(index*25).Take(25) }) });
            Chunk(0);
            Assert.That(editor.Draft.Count, Is.EqualTo(originalCount), "Partial uploads must not replace a local draft");
            Chunk(2); Chunk(1);
            Assert.That(editor.Draft.Count, Is.EqualTo(originalCount), "Out-of-order uploads must be rejected");
            upload = Guid.NewGuid();
            for(var index=0;index<16;index++) Chunk(index);
            Assert.That(editor.Draft.Count, Is.EqualTo(400), "A complete bounded upload must reach the preview without the old bridge limit");
            typeof(ApartmentEditorSystem).GetMethod("OnAction", flags)!.Invoke(pair.Client.EntMan.System<ApartmentEditorSystem>(), new object[] { "close", "{}" });
        });
        await pair.CleanReturnAsync();
    }
}

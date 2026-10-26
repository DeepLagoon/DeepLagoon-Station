using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Content.Server._DeepLagoon.InteractionPanel;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared.Humanoid;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class InteractionPanelSexTests
{
    [TestCase("penis", InteractionPanelSexTraits.Male)]
    [TestCase("testicles", InteractionPanelSexTraits.Male)]
    [TestCase("vagina", InteractionPanelSexTraits.Female)]
    [TestCase("womb", InteractionPanelSexTraits.Female)]
    public void PrimaryFeaturesAddCompatibilityEvenWhenHidden(string part, InteractionPanelSexTraits expected)
    {
        Assert.That(InteractionPanelSexRules.GetTraits(null, [new BodyFeature { Part=part, Enabled=true, Visibility=0 }]), Is.EqualTo(expected));
        Assert.That(InteractionPanelSexRules.GetTraits(null, [new BodyFeature { Part=part, Enabled=false }]), Is.EqualTo(InteractionPanelSexTraits.None));
    }

    [Test]
    public void SelectedSexAndBothPrimarySetsAreAlternativesWithoutInferringFromSecondaryFeatures()
    {
        var features = new[] { new BodyFeature { Part="penis", Enabled=true }, new BodyFeature { Part="vagina", Enabled=true } };
        Assert.That(InteractionPanelSexRules.GetTraits(Sex.Unsexed,features), Is.EqualTo(InteractionPanelSexTraits.Unsexed|InteractionPanelSexTraits.Male|InteractionPanelSexTraits.Female));
        Assert.That(InteractionPanelSexRules.GetTraits(Sex.Female), Is.EqualTo(InteractionPanelSexTraits.Female));
        Assert.That(InteractionPanelSexRules.GetTraits(null,[new BodyFeature { Part="breasts", Enabled=true },new BodyFeature { Part="butt", Enabled=true },new BodyFeature { Part="belly", Enabled=true }]), Is.EqualTo(InteractionPanelSexTraits.None));
    }

    [Test]
    public void RestrictionsRemainIndependentForInitiatorAndTarget()
    {
        var action = new InteractionPanelActionDefinition("sex-test","all",InteractionPanelCategory.Neutral,UserSexes:[Sex.Male],TargetSexes:[Sex.Female]);
        Assert.That(action.SupportsSexTraits(InteractionPanelSexTraits.Male,InteractionPanelSexTraits.Female), Is.True);
        Assert.That(action.SupportsSexTraits(InteractionPanelSexTraits.Female,InteractionPanelSexTraits.Male), Is.False);
        Assert.That(action.SupportsSexes(Sex.Male,Sex.Female), Is.True, "Selected sex remains sufficient without enabled parts");
        Assert.That(action.SupportsSexes(null,Sex.Female), Is.False);
        Assert.That((action with { UserSexes=[], TargetSexes=null }).SupportsSexTraits(InteractionPanelSexTraits.Male|InteractionPanelSexTraits.Female,InteractionPanelSexTraits.None), Is.False);
        Assert.That((action with { UserSexes=null, TargetSexes=null }).SupportsSexes(null,null), Is.True);
        Assert.That((action with { UserSexes=[Sex.Unsexed],TargetSexes=null }).SupportsSexTraits(InteractionPanelSexTraits.None,InteractionPanelSexTraits.None), Is.False, "Missing anatomy does not imply the selected Unsexed value");
    }

    [Test]
    public async Task ServerChecksCurrentAnatomyAndUpdatesAnOpenClientPanelAndRussianDescriptions()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive=true, Connected=true });
        var map=await pair.CreateTestMap();
        EntityUid body=default;
        object originalWindow=null!;
        var panelField=typeof(Content.Client._DeepLagoon.InteractionPanel.InteractionPanelSystem).GetField("_panel",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var dataField=typeof(Content.Client._DeepLagoon.InteractionPanel.InteractionPanelWindow).GetField("_data",BindingFlags.Instance|BindingFlags.NonPublic)!;
        await pair.Server.WaitAssertion(() =>
        {
            var e=pair.Server.EntMan;
            var folder=Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory,"..","..","Tools","lustwish"));
            var descriptions=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(folder,"descriptions.ru.json")))!;
            var originals=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"source_metadata.json"))).RootElement.GetProperty("items");
            var prototypes=pair.Server.ResolveDependency<IPrototypeManager>();
            foreach(var (id,description) in descriptions)
            {
                var prototype=prototypes.Index<EntityPrototype>(id);
                Assert.That(prototype.Description, Is.EqualTo(description), id+" Russian description");
                Assert.That(prototype.Name, Is.EqualTo(originals.GetProperty(id).GetProperty("name").GetString()), id+" original name");
            }
            body=e.SpawnEntity("MobHuman",new EntityCoordinates(map.Grid,new Vector2(.5f,.5f)));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(),body);
            var appearance=e.GetComponent<HumanoidAppearanceComponent>(body);
            appearance.Sex=Sex.Unsexed;
            appearance.BodyFeatures=BodyCustomizationCatalog.Sanitize([new BodyFeature {Part="vagina",Enabled=true,Visibility=0}]);
            e.Dirty(body,appearance);
            e.System<InteractionPanelSystem>().OpenPanel(body,body);
        });
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
        {
            var window=panelField.GetValue(pair.Client.EntMan.System<Content.Client._DeepLagoon.InteractionPanel.InteractionPanelSystem>())!;
            Assert.That(window, Is.Not.Null);
            originalWindow=window;
            var data=(InteractionPanelPanelEvent)dataField.GetValue(window)!;
            Assert.That(data.UserSexTraits, Is.EqualTo(InteractionPanelSexTraits.Unsexed|InteractionPanelSexTraits.Female));
            Assert.That(data.TargetSexTraits, Is.EqualTo(data.UserSexTraits));
        });
        await pair.Server.WaitAssertion(() =>
        {
            var e=pair.Server.EntMan;
            var appearance=e.GetComponent<HumanoidAppearanceComponent>(body);
            var system=e.System<InteractionPanelSystem>();
            var allowed=typeof(InteractionPanelSystem).GetMethod("Allowed",BindingFlags.NonPublic|BindingFlags.Instance)!;
            var female=new InteractionPanelActionDefinition("test","all",InteractionPanelCategory.Neutral,InteractionPanelActionFlags.INTERACTION_SELF|InteractionPanelActionFlags.INTERACTION_HUMANOID,UserSexes:[Sex.Female],TargetSexes:[Sex.Female]);
            Assert.That(allowed.Invoke(system,[body,body,female]), Is.EqualTo(true));
            appearance.BodyFeatures[0].Enabled=false;
            Assert.That(allowed.Invoke(system,[body,body,female]), Is.EqualTo(false), "Execution immediately rejects anatomy removed since opening the panel");
            appearance.BodyFeatures=BodyCustomizationCatalog.Sanitize([new BodyFeature {Part="penis",Enabled=true}]);
            e.Dirty(body,appearance);
            var changed=new BodyCustomizationChangedEvent();
            e.EventBus.RaiseLocalEvent(body,ref changed);
        });
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
        {
            var window=panelField.GetValue(pair.Client.EntMan.System<Content.Client._DeepLagoon.InteractionPanel.InteractionPanelSystem>())!;
            Assert.That(window, Is.SameAs(originalWindow), "Updating conditions preserves the open window");
            var data=(InteractionPanelPanelEvent)dataField.GetValue(window)!;
            Assert.That(data.UserSexTraits, Is.EqualTo(InteractionPanelSexTraits.Unsexed|InteractionPanelSexTraits.Male));
            Assert.That(data.TargetSexTraits, Is.EqualTo(data.UserSexTraits));
        });
        await pair.Server.WaitPost(() => pair.Server.EntMan.System<SharedHumanoidAppearanceSystem>().SetSex(body,Sex.Female));
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
        {
            var data=(InteractionPanelPanelEvent)dataField.GetValue(originalWindow)!;
            Assert.That(data.UserSexTraits, Is.EqualTo(InteractionPanelSexTraits.Female|InteractionPanelSexTraits.Male), "Changing selected sex also updates compatibility");
        });
        await pair.Server.WaitPost(() => pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(),null));
        await pair.Server.WaitPost(() => pair.Server.EntMan.DeleteEntity(map.MapUid));
        await pair.ReallyBeIdle();
        await pair.CleanReturnAsync();
    }
}

using System.Reflection;
using Content.Client._DV.Options.UI.Tabs;
using Content.Client.Options.UI;
using Robust.Client.UserInterface.Controls;
using System.Linq;
using Content.Shared.Audio.Jukebox;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Client.GameStates;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using ClientUi = Robust.Client.GameObjects.UserInterfaceSystem;
using ServerUi = Robust.Server.GameObjects.UserInterfaceSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class PredictionInterfaceTests
{
    [Test]
    public async Task AdvancedOptionsApplyReloadAndResetPredictionParameters()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Client.WaitAssertion(() =>
        {
            var cfg = pair.Client.CfgMan;
            cfg.SetCVar(CVars.NetPredict, false);
            cfg.SetCVar(CVars.NetPredictTickBias, 3);
            cfg.SetCVar(CVars.NetPredictLagBias, .025f);
            cfg.SetCVar(CVars.NetInterpCorrectionHalfLife, .150f);
            using var tab = new DeltaTab();
            var prediction = tab.FindControl<CheckBox>("ClientPredictionCheckBox");
            var ticks = tab.FindControl<OptionSlider>("PredictionTicksSlider").Slider;
            var lag = tab.FindControl<OptionSlider>("PredictionLagSlider").Slider;
            var smoothing = tab.FindControl<OptionSlider>("PredictionSmoothingSlider").Slider;
            void Press(string action) => typeof(OptionsTabControlRow)
                .GetMethod(action + "ButtonPressed", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(tab.Control, [null]);

            Assert.That(prediction.Pressed, Is.False);
            Assert.That(ticks.Disabled && lag.Disabled && smoothing.Disabled, Is.True);
            Assert.That(ticks.Value, Is.EqualTo(3));
            Assert.That(lag.Value, Is.EqualTo(25).Within(.001));
            Assert.That(smoothing.Value, Is.EqualTo(150).Within(.001));
            // Reset must also unlock the sliders when it restores enabled prediction.
            Press("Default");
            Assert.That(prediction.Pressed, Is.True);
            Assert.That(ticks.Disabled || lag.Disabled || smoothing.Disabled, Is.False);
            Assert.That(ticks.Value, Is.EqualTo(CVars.NetPredictTickBias.DefaultValue));
            Assert.That(lag.Value * .001f, Is.EqualTo(CVars.NetPredictLagBias.DefaultValue).Within(.0001));
            Press("Reset");
            Assert.That(prediction.Pressed, Is.False);
            Assert.That(ticks.Disabled && lag.Disabled && smoothing.Disabled, Is.True);
            Press("Default");
            ticks.Value = 8;
            lag.Value = 100;
            smoothing.Value = 300;
            Press("Apply");
            Assert.That(cfg.GetCVar(CVars.NetPredict), Is.True);
            Assert.That(cfg.GetCVar(CVars.NetPredictTickBias), Is.EqualTo(8));
            Assert.That(cfg.GetCVar(CVars.NetPredictLagBias), Is.EqualTo(.1f).Within(.0001));
            Assert.That(cfg.GetCVar(CVars.NetInterpCorrectionHalfLife), Is.EqualTo(.3f).Within(.0001));
            cfg.SetCVar(CVars.NetPredict, false);
            tab.Control.ReloadValues();
            Assert.That(prediction.Pressed, Is.False);
            Assert.That(ticks.Disabled && lag.Disabled && smoothing.Disabled, Is.True);
            Assert.That(ticks.Value, Is.EqualTo(8), "Disabling preserves the selected strength.");
        });
        await pair.CleanReturnAsync();
    }
    [TestCase(1)]
    [TestCase(2)]
    public async Task InterfacesOpenCloseAndReopenAcrossPredictionChanges(int style)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        var map = await pair.CreateTestMap();
        EntityUid actor = default, device = default;
        await pair.Client.WaitPost(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(WebUiCVars.InterfaceStyle, style);
            cfg.SetCVar(WebUiCVars.WindowStyles, "");
        });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            actor = entities.SpawnEntity("MobHuman", map.GridCoords);
            device = entities.SpawnEntity("Jukebox", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), actor);
        });
        var netDevice = pair.Server.EntMan.GetNetEntity(device);
        await pair.RunTicksSync(30);

        // Include both directions of a live settings change and repeated opens while disabled.
        foreach (var predict in new[] { true, false, false, true })
        {
            await pair.Client.WaitPost(() => pair.Client.CfgMan.SetCVar(CVars.NetPredict, predict));
            await pair.RunTicksSync(3);
            if (predict)
            {
                await pair.Server.WaitPost(() => pair.Server.EntMan.System<ServerUi>()
                    .OpenUi(device, JukeboxUiKey.Key, actor));
            }
            else
            {
                // Exercise the real client request and server reply, with prediction still fully off.
                await pair.Client.WaitPost(() => pair.Client.EntMan.System<ClientUi>()
                    .OpenUi(pair.Client.EntMan.GetEntity(netDevice), JukeboxUiKey.Key, predicted: true));
            }
            await pair.RunTicksSync(30);
            BoundUserInterface bui = null;
            await pair.Client.WaitAssertion(() =>
            {
                Assert.That(pair.Client.ResolveDependency<IClientGameStateManager>().IsPredictionEnabled, Is.EqualTo(predict));
                Assert.That(pair.Client.EntMan.System<ClientUi>()
                    .TryGetOpenUi(pair.Client.EntMan.GetEntity(netDevice), JukeboxUiKey.Key, out bui), Is.True);
                Assert.That(bui.IsOpened, Is.True, "The replicated BUI must actually run Open, not remain queued.");
                bui.Close();
            });
            await pair.RunTicksSync(30);
            await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.System<ServerUi>()
                .IsUiOpen(device, JukeboxUiKey.Key, actor), Is.False));
            await pair.Client.WaitAssertion(() => Assert.That(pair.Client.EntMan.System<ClientUi>()
                .TryGetOpenUi(pair.Client.EntMan.GetEntity(netDevice), JukeboxUiKey.Key, out _), Is.False,
                "Closing must also dispose and remove the interface while prediction is off."));
        }
        await pair.CleanReturnAsync();
    }
}


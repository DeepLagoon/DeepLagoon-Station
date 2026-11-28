using System.Globalization;
using System.Linq;
using Content.Shared._DeepLagoon.Voyages;
using Content.Shared._NF.CCVar;
using Content.Shared.Salvage;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Salvage.Expeditions.Modifiers;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using NativeExpedition = Content.Client.Salvage.SalvageExpeditionComponent;
namespace Content.Client._DeepLagoon.Voyages;

public sealed partial class SectorExpeditionWindow
{
    private readonly IEntityManager _detailEntities = IoCManager.Resolve<IEntityManager>();
    private readonly IPrototypeManager _detailPrototypes = IoCManager.Resolve<IPrototypeManager>();
    private readonly IConfigurationManager _detailConfig = IoCManager.Resolve<IConfigurationManager>();
    private readonly IGameTiming _detailClock = IoCManager.Resolve<IGameTiming>();
    private readonly IPlayerManager _detailPlayers = IoCManager.Resolve<IPlayerManager>();
    private Label? _liveMission;
    private TimeSpan _lastDetailFrame, _nextMissionText;

    private void AddMissionDetails(BoxContainer card, SectorPlanetView planet, SectorExpeditionSnapshot state)
    {
        var salvage = _detailEntities.System<SharedSalvageSystem>();
        var parameters = SectorResearchDefinition.MissionParameters(state.CurrentSector, planet.Index, state.Map.Seed);
        var mission = salvage.GetMission(parameters.MissionType, parameters.Difficulty, parameters.Seed);
        void Field(string title, string value, string name)
        {
            var heading = Text(title); heading.FontColorOverride = Color.FromHex("#90A8B3"); card.AddChild(heading);
            var text = new RichTextLabel { HorizontalExpand = true, Name = name };
            text.SetMessage(value); card.AddChild(text);
        }
        Field("Тип экспедиции", Loc.GetString("salvage-expedition-type-" + parameters.MissionType), "SectorMissionType");
        Field(Loc.GetString("salvage-expedition-window-difficulty"), Loc.GetString("salvage-expedition-difficulty-" + parameters.Difficulty), "SectorMissionDifficulty");
        Field(Loc.GetString("salvage-expedition-window-details"), salvage.GetMissionDescription(mission), "SectorMissionObjective");
        var faction = _detailPrototypes.TryIndex<SalvageFactionPrototype>(mission.Faction, out var enemy) && Loc.TryGetString(enemy.Description, out var localized) ? localized : mission.Faction;
        Field(Loc.GetString("salvage-expedition-window-hostiles"), faction, "SectorMissionHostiles");
        Field(Loc.GetString("salvage-expedition-window-duration"), Loc.GetString("salvage-expedition-duration-unlimited"), "SectorMissionDuration");
        Field(Loc.GetString("salvage-expedition-window-biome"), Loc.GetString(_detailPrototypes.Index<SalvageBiomeModPrototype>(mission.Biome).ID), "SectorMissionBiome");
        Field(Loc.GetString("salvage-expedition-window-modifiers"), mission.Modifiers.Count == 0 ? "Нет" : string.Join("\n", mission.Modifiers.Select(m => "• " + m)), "SectorMissionModifiers");
        var rewardsEnabled = _detailConfig.GetCVar(NFCCVars.SalvageExpeditionRewardsEnabled);
        if (rewardsEnabled && mission.Rewards.Count > 0)
        {
            var rewards = mission.Rewards.GroupBy(id => id).Select(g =>
                (_detailPrototypes.TryIndex<EntityPrototype>(g.Key, out var item) ? item.Name : g.Key) + (g.Count() > 1 ? " ×" + g.Count() : ""));
            Field(Loc.GetString("salvage-expedition-window-rewards"), string.Join("\n", rewards.Select(r => "• " + r)), "SectorMissionRewards");
        }
        else Field(Loc.GetString("salvage-expedition-window-rewards"), "Выплаты экспедиций отключены", "SectorMissionRewards");
        if (!planet.Completed)
        {
            var reserve = state.Orders.Where(o => o.Sector == state.CurrentSector && o.Planets > o.Completed)
                .Sum(o => (o.Remaining + o.Planets - o.Completed - 1) / (o.Planets - o.Completed));
            if (reserve > 0) Field("Награда за исследование планеты", Money(reserve), "SectorMissionResearchReward");
        }
        if (state.OnPlanet && state.ActivePlanet == planet.Index)
        {
            _liveMission = Text(""); _liveMission.Name = "SectorMissionLiveStatus"; card.AddChild(_liveMission);
            UpdateMissionStatus();
        }
    }
    private static string FormatDuration(TimeSpan duration)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling(duration.TotalSeconds));
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }
    private void UpdateMissionStatus()
    {
        if (_liveMission == null || _state is not { OnPlanet: true } state) return;
        var counters = $"Цели: {state.MissionDone}/{state.MissionTotal}";
        if (_detailEntities.TryGetComponent<TransformComponent>(_detailPlayers.LocalEntity, out var transform) &&
            _detailEntities.TryGetComponent<NativeExpedition>(transform.MapUid, out var expedition))
        {
            counters = $"Цели: {expedition.ObjectivesCompleted}/{expedition.ObjectivesTotal}";
            _liveMission.Text = expedition.Stage == ExpeditionStage.Added ? "Подготовка экспедиции" : expedition.Stage == ExpeditionStage.Running
                ? Loc.GetString("salvage-expedition-status-unlimited", ("completed", expedition.ObjectivesCompleted), ("total", expedition.ObjectivesTotal))
                : $"Эвакуация: {FormatDuration(expedition.EndTime - _detailClock.CurTime)} · {counters}";
        }
        else _liveMission.Text = counters;
    }
    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        var now = _detailClock.RealTime;
        if (_lastDetailFrame != TimeSpan.Zero && VisibleInTree) Model.Advance((now - _lastDetailFrame).TotalSeconds);
        _lastDetailFrame = now;
        if (now < _nextMissionText) return;
        _nextMissionText = now + TimeSpan.FromSeconds(1); UpdateMissionStatus();
    }
}

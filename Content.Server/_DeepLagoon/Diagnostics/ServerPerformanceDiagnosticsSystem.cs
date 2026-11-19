using Content.Server.GameTicking;
using Content.Shared._Goobstation.ItemMiner;
using Content.Shared.Body.Organ;
using Content.Shared.Materials;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Components;
using Content.Shared.NPC;
using Content.Shared.Stacks;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Diagnostics;

/// <summary>Constant-time component registry counts; never enumerates world entities for logging.</summary>
public sealed partial class ServerPerformanceDiagnosticsSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private GameTicker _ticker = default!;
    private bool _enabled;
    private TimeSpan _interval = TimeSpan.FromSeconds(30);
    private TimeSpan _next;

    public override void Initialize()
    {
        base.Initialize();
        Subs.CVar(_config, Robust.Shared.CVars.PerformanceLogLevel, value => { _enabled = value > 0; _next = default; }, true);
        Subs.CVar(_config, Robust.Shared.CVars.PerformanceLogInterval, value =>
        {
            _interval = TimeSpan.FromSeconds(float.IsFinite(value) ? Math.Clamp(value, 5, 600) : 30);
            _next = default;
        }, true);
    }

    public override void Update(float frameTime)
    {
        if (!_enabled || _timing.RealTime < _next) return;
        _next = _timing.RealTime + _interval;
        if (!Log.IsLogLevelEnabled(Robust.Shared.Log.LogLevel.Info)) return;
        Log.Info(FormattableString.Invariant(
            $"Performance content registry: round={_ticker.RoundId} run_level={_ticker.RunLevel} entities={EntityManager.EntityCount} mobs={EntityManager.Count<MobStateComponent>()} active_npc={EntityManager.Count<ActiveNPCComponent>()} movers={EntityManager.Count<InputMoverComponent>()} active_movers={EntityManager.Count<ActiveInputMoverComponent>()} miners={EntityManager.Count<ItemMinerComponent>()} stacks={EntityManager.Count<StackComponent>()} materials={EntityManager.Count<MaterialComponent>()} organs={EntityManager.Count<OrganComponent>()} includes_paused_and_pending_removal=True"));
    }
}

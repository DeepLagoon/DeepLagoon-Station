using Content.Server.Actions;
using Content.Server.Popups;
using Content.Shared._DeepLagoon.Species.Ariral;
using Content.Shared.Mobs.Systems;
using Content.Shared.Storage;
using Robust.Server.Audio;
using Robust.Shared.Player;
using Robust.Shared.Random;

namespace Content.Server._DeepLagoon.Species.Ariral;

/// <summary>
/// Grants the source race's single-use egg action without automatic NPC egg production.
/// </summary>
public sealed partial class AriralEggLayerSystem : EntitySystem
{
    [Dependency] private ActionsSystem _actions = default!;
    [Dependency] private AudioSystem _audio = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private IRobustRandom _random = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<EggAriralLayerComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<EggAriralLayerComponent, EggAriralLayInstantActionEvent>(OnLayEgg);
    }

    private void OnMapInit(EntityUid uid, EggAriralLayerComponent component, MapInitEvent args)
    {
        _actions.AddAction(uid, ref component.Action, component.EggLayAction);
    }

    private void OnLayEgg(EntityUid uid, EggAriralLayerComponent component, EggAriralLayInstantActionEvent args)
    {
        if (args.Handled || _mobState.IsDead(uid))
            return;

        foreach (var prototype in EntitySpawnCollection.GetSpawns(component.EggSpawn, _random))
            Spawn(prototype, Transform(uid).Coordinates);

        _audio.PlayPvs(component.EggLaySound, uid);
        _popup.PopupEntity(Loc.GetString("action-popup-lay-egg-user"), uid, uid);
        _popup.PopupEntity(Loc.GetString("action-popup-lay-egg-others", ("entity", uid)), uid, Filter.PvsExcept(uid), true);
        args.Handled = true;
    }
}

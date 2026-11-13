using System.Reflection.Metadata.Ecma335;
using Content.Shared._Mono.ShipGuns;
using Content.Shared.Construction;
using Content.Shared.Construction.Components;
using Content.Shared.Construction.EntitySystems;
using Content.Shared.Destructible;
using Content.Shared.Examine;
using Content.Shared.Popups;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;

namespace Content.Shared._Crescent.Hardpoints;

/// <summary>
/// This handles...
/// </summary>
public partial class SharedHardpointSystem : EntitySystem
{
    [Dependency] private SharedTransformSystem _transformSystem = default!;
    private SharedMapSystem _mapSystem => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private EntityManager _entMan = default!;

    //used for logging, don't touch this
    private ISawmill _sawmill = default!;
    /// <inheritdoc/>
    public override void Initialize()
    {
        SubscribeLocalEvent<HardpointAnchorableOnlyComponent, AnchorStateChangedEvent>(OnAnchorChange);
        SubscribeLocalEvent<HardpointAnchorableOnlyComponent, MapInitEvent>(OnMapLoad);
        SubscribeLocalEvent<HardpointComponent, AnchorStateChangedEvent>(OnHardpointAnchor);
        SubscribeLocalEvent<HardpointAnchorableOnlyComponent, ComponentRemove>(OnShipgunRemove);
        SubscribeLocalEvent<HardpointComponent, ComponentRemove>(OnHardpointRemove);
        SubscribeLocalEvent<HardpointAnchorableOnlyComponent, ExaminedEvent>(OnExamined);
        // TODO: ACCOUNT FOR REMOVING IT IN ADMIN MODE
        _sawmill = IoCManager.Resolve<ILogManager>().GetSawmill("crescent.hardpoints");
    }

    public void OnMapLoad(EntityUid uid, HardpointAnchorableOnlyComponent comp, ref MapInitEvent args)
    {
        if (Transform(uid).MapUid == null)
            return;
        if (TryAnchorToHardpoint(uid, comp))
            return;
        _sawmill.Debug(
            $"Hardpoint-only weapon had no hardpoint under itself at mapInit. {uid} , {MetaData(uid).EntityName}");
    }
    public void OnAnchorChange(EntityUid uid, HardpointAnchorableOnlyComponent component, ref AnchorStateChangedEvent args)
    {
        //this is here to prevent this code from running at the start of the round OR when you spawn a new ship.
        //otherwise, targeting computers do not see turrets. only sometimes.
        if (_entMan.GetComponent<MetaDataComponent>(uid).EntityLifeStage != EntityLifeStage.MapInitialized)
            return;
        //_sawmill.Debug("ON ANCHOR CHANGE RAN" + args.Anchored.ToString());
        //LOGIC:
        /*
        "im a shipgun"
        "i just got anchored or deanchored!"
        "if i got anchored, let me check if I've got a valid hardpoint under me."
            "if yes, then set the values properly and stay anchored."
            "if no, then and send a popup. the values were never set, so the gun can't fire anyway."
        "if i just got deanchored,"
            "de-set all the values, then de-anchor the gun too."
        */
        if (args.Anchored)
        {
            if (TryAnchorToHardpoint(uid, component)) //if it's a valid hardpoint, then we're good. this function also sets the values properly.
            {
                return;
            }
            else
            {
                //_transformSystem.Unanchor(uid); //if it's not / we dont have a hardpoint under it, kick that shit out
                //play sound effect
                _popup.PopupPredicted(Loc.GetString("WARNING! This weapon is not mounted on a compatible hardpoint and will not function!"), uid, null);
                return;
            }
        }

        //else, if we UNanchored
        if (component.anchoredTo == null || TerminatingOrDeleted(uid)) //this should literally never happen
        {
            return;
        }

        Deanchor(uid, component.anchoredTo.Value, args.Transform.GridUid, component);
    }

    public void OnHardpointAnchor(EntityUid target, HardpointComponent comp, ref AnchorStateChangedEvent args)
    {
        if (args.Anchored || comp.anchoring is not { } weapon || TerminatingOrDeleted(target))
            return;

        if (!TryComp<HardpointAnchorableOnlyComponent>(weapon, out var mounted) || mounted.anchoredTo != target)
        {
            ClearHardpoint(target, comp, weapon, args.Transform.GridUid);
            return;
        }

        Deanchor(weapon, target, args.Transform.GridUid, mounted);
        if (!TerminatingOrDeleted(weapon) && TryComp(weapon, out TransformComponent? weaponTransform))
            _transformSystem.Unanchor(weapon, weaponTransform);
    }

    private void Deanchor(EntityUid target, EntityUid anchor, EntityUid? grid, HardpointAnchorableOnlyComponent component)
    {
        component.anchoredTo = null;
        if (!TerminatingOrDeleted(target) && component.Running)
            Dirty(target, component);

        if (TryComp<HardpointComponent>(anchor, out var hardpoint) && hardpoint.anchoring == target)
            ClearHardpoint(anchor, hardpoint, target, grid);
    }

    private void ClearHardpoint(EntityUid anchor, HardpointComponent hardpoint, EntityUid weapon, EntityUid? grid)
    {
        hardpoint.anchoring = null;
        if (TerminatingOrDeleted(anchor))
            return;

        if (hardpoint.Running)
            Dirty(anchor, hardpoint);
        if (grid is { } gridUid)
            RaiseLocalEvent(anchor, new HardpointCannonDeanchoredEvent { CannonUid = weapon, gridUid = gridUid });
    }

    private void OnShipgunRemove(EntityUid uid, HardpointAnchorableOnlyComponent component, ComponentRemove args)
    {
        if (component.anchoredTo is { } hardpoint)
            Deanchor(uid, hardpoint, CompOrNull<TransformComponent>(hardpoint)?.GridUid, component);
    }

    private void OnHardpointRemove(EntityUid uid, HardpointComponent component, ComponentRemove args)
    {
        if (component.anchoring is not { } weapon ||
            !TryComp<HardpointAnchorableOnlyComponent>(weapon, out var mounted) || mounted.anchoredTo != uid)
            return;

        Deanchor(weapon, uid, CompOrNull<TransformComponent>(uid)?.GridUid, mounted);
        if (!TerminatingOrDeleted(weapon) && TryComp(weapon, out TransformComponent? weaponTransform))
            _transformSystem.Unanchor(weapon, weaponTransform);
    }


    /// <summary>
    /// Returns true/false based on if we are able to anchor something here or not.
    /// TRUE: we're good to anchor here, hardpoint fits/exists.
    /// FALSE: there is no hardpoint/it's of the wrong type
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="component"></param>
    /// <returns></returns>
    public bool TryAnchorToHardpoint(EntityUid uid, HardpointAnchorableOnlyComponent component)
    {
        //_sawmill.Debug("TRY ANCHOR TO HARDPOINT RAN");
        var gridUid = Transform(uid).GridUid;
        if (gridUid is null)
            return false;
        if (!TryComp<MapGridComponent>(gridUid, out var gridComp))
            return false;
        if (!_transformSystem.TryGetGridTilePosition(uid, out var indice, gridComp))
        {
            return false;
        }

        foreach (var entity in _mapSystem.GetAnchoredEntities(new Entity<MapGridComponent>(gridUid.Value, gridComp), indice))
        {
            if (!TryComp<HardpointComponent>(entity, out var hardComp))
                continue;
            if (hardComp.anchoring is not null)
                continue;
            if ((hardComp.CompatibleTypes & component.CompatibleTypes) == 0)
                continue;
            if (hardComp.CompatibleSizes < component.CompatibleSizes)
                continue;
            AnchorEntityToHardpoint(uid, entity, component, hardComp, gridUid.Value);
            return true;
        }

        return false;
    }

    private void OnExamined(Entity<HardpointAnchorableOnlyComponent> ent, ref ExaminedEvent args)
    {
        var sizeColor = Color.Yellow;
        var typeColor = Color.CornflowerBlue;

        // WELCOME TO HARDCODE CENTRAL...
        // I don't care enough to make this definable anywhere else. This would be really easy to make prototypable, but we don't need more than 5 sizes.
        // Sorry ilya.
        switch (ent.Comp.CompatibleSizes)
        {
            case weaponSizes.Superlight:
                sizeColor = Color.LightSkyBlue;
                break;
            case weaponSizes.Light:
                sizeColor = Color.MediumSpringGreen;
                break;
            case weaponSizes.Medium:
                sizeColor = Color.Gold;
                break;
            case weaponSizes.Heavy:
                sizeColor = Color.DarkOrange;
                break;
            case weaponSizes.Superheavy:
                sizeColor = Color.Crimson;
                break;
        }
        switch (ent.Comp.CompatibleTypes)
        {
            case weaponTypes.Ballistic:
                typeColor = Color.Goldenrod;
                break;
            case weaponTypes.Energy:
                typeColor = Color.DeepPink;
                break;
            case weaponTypes.Missile:
                typeColor = Color.Lime;
                break;
            case weaponTypes.Universal:
                typeColor = Color.White;
                break;
        }

        args.PushMarkup(Loc.GetString("gunnery-hardpoint-examine-detail", ("size", ent.Comp.CompatibleSizes), ("sizeColor", sizeColor),  ("type", ent.Comp.CompatibleTypes), ("typeColor", typeColor)));
    }

    public void AnchorEntityToHardpoint(EntityUid target, EntityUid anchor, HardpointAnchorableOnlyComponent targetComp, HardpointComponent hardpoint, EntityUid grid)
    {
        //_sawmill.Debug("ANCHOR ENTITY TO HARDPOINT RAN");
        hardpoint.anchoring = target;
        targetComp.anchoredTo = anchor;
        _transformSystem.SetLocalRotation(target, Transform(anchor).LocalRotation);
        HardpointCannonAnchoredEvent arg = new();
        arg.cannonUid = target;
        arg.gridUid = grid;
        RaiseLocalEvent(anchor, arg);
        DirtyEntity(target);
        DirtyEntity(anchor);
        //Dirty(target, targetComp);
    }
}

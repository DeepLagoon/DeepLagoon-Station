using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Content.Shared.DoAfter;
using Content.Shared.Doors.Components;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.SprayPainter.Components;
using Content.Shared.SprayPainter.Prototypes;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Prototypes;
using System.Linq;
using Content.Shared.Decals;
using Content.Shared.Interaction.Events;
using Content.Shared.UserInterface;

namespace Content.Shared.SprayPainter;

/// <summary>
/// System for painting airlocks using a spray painter.
/// Pipes are handled serverside since AtmosPipeColorSystem is server only.
/// </summary>
public abstract partial class SharedSprayPainterSystem : EntitySystem
{
    [Dependency] protected IPrototypeManager Proto = default!;
    [Dependency] private   ISharedAdminLogManager _adminLogger = default!;
    [Dependency] protected SharedAppearanceSystem Appearance = default!;
    [Dependency] protected SharedAudioSystem Audio = default!;
    [Dependency] protected SharedDoAfterSystem DoAfter = default!;
    [Dependency] private   SharedPopupSystem _popup = default!;

    public List<AirlockStyle> Styles { get; private set; } = new();
    public List<AirlockGroupPrototype> Groups { get; private set; } = new();

    private static readonly ProtoId<AirlockDepartmentsPrototype> Departments = "Departments";

    public override void Initialize()
    {
        base.Initialize();

        CacheStyles();

        SubscribeLocalEvent<SprayPainterComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<SprayPainterComponent, SprayPainterDoorDoAfterEvent>(OnDoorDoAfter);
        Subs.BuiEvents<SprayPainterComponent>(SprayPainterUiKey.Key, subs =>
        {
            subs.Event<SprayPainterSpritePickedMessage>(OnSpritePicked);
            subs.Event<SprayPainterColorPickedMessage>(OnColorPicked);
            subs.Event<SprayPainterCustomColorMessage>(OnCustomColorPicked);
            subs.Event<SprayPainterDecalSettingsMessage>(OnDecalSettings);
        });

        SubscribeLocalEvent<PaintableAirlockComponent, InteractUsingEvent>(OnAirlockInteract);
        SubscribeLocalEvent<SprayPainterComponent, UseInHandEvent>(OnUseInHand, before: new[] { typeof(ActivatableUISystem) });

        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
    }

    private void OnMapInit(Entity<SprayPainterComponent> ent, ref MapInitEvent args)
    {
        if (string.IsNullOrEmpty(ent.Comp.DecalId))
            ent.Comp.DecalId = Proto.EnumeratePrototypes<DecalPrototype>().Where(p => p.ShowMenu).OrderBy(p => p.ID).FirstOrDefault()?.ID ?? "";
        if (ent.Comp.ColorPalette.Count == 0)
            return;

        SetColor(ent, ent.Comp.ColorPalette.First().Key);
    }

    private void OnDoorDoAfter(Entity<SprayPainterComponent> ent, ref SprayPainterDoorDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;

        if (args.Args.Target is not {} target)
            return;

        if (!TryComp<PaintableAirlockComponent>(target, out var airlock))
            return;

        airlock.Department = args.Department;
        airlock.PaintColor = args.Color;
        Dirty(target, airlock);

        Audio.PlayPredicted(ent.Comp.SpraySound, ent, args.Args.User);
        Appearance.SetData(target, DoorVisuals.BaseRSI, args.Sprite);
        _adminLogger.Add(LogType.Action, LogImpact.Low, $"{ToPrettyString(args.Args.User):user} painted {ToPrettyString(args.Args.Target.Value):target}");

        args.Handled = true;
    }

    #region UI messages

    private void OnUseInHand(EntityUid uid, SprayPainterComponent component, UseInHandEvent args)
    {
        if (args.Handled)
            return;
        component.Mode = (SprayPainterMode) (((int) component.Mode + 1) % 3);
        Dirty(uid, component);
        _popup.PopupClient(Loc.GetString("spray-painter-mode-changed", ("mode", Loc.GetString(ModeLocKey(component.Mode)))), uid, args.User);
        args.Handled = true;
    }

    public static string ModeLocKey(SprayPainterMode mode) => mode switch
    {
        SprayPainterMode.Airlocks => "spray-painter-mode-airlocks",
        SprayPainterMode.Pipes => "spray-painter-mode-pipes",
        _ => "spray-painter-mode-decals",
    };

    public static bool ValidColor(Color color) => float.IsFinite(color.R) && float.IsFinite(color.G)
        && float.IsFinite(color.B) && float.IsFinite(color.A) && color.R is >= 0 and <= 1
        && color.G is >= 0 and <= 1 && color.B is >= 0 and <= 1 && color.A is >= 0 and <= 1;

    private void OnCustomColorPicked(Entity<SprayPainterComponent> ent, ref SprayPainterCustomColorMessage args)
    {
        if (!ValidColor(args.Color))
            return;
        switch (ent.Comp.Mode)
        {
            case SprayPainterMode.Airlocks: ent.Comp.AirlockColor = args.Color; break;
            case SprayPainterMode.Pipes: ent.Comp.PipeColor = args.Color; ent.Comp.PickedColor = null; break;
            case SprayPainterMode.Decals: ent.Comp.DecalColor = args.Color; break;
        }
        Dirty(ent);
    }

    private void OnDecalSettings(Entity<SprayPainterComponent> ent, ref SprayPainterDecalSettingsMessage args)
    {
        if (ent.Comp.Mode != SprayPainterMode.Decals || !ValidDecal(args.Decal))
            return;
        ent.Comp.DecalId = args.Decal.Id;
        ent.Comp.DecalColor = args.Decal.Color ?? Color.White;
        ent.Comp.DecalRotation = (float) args.Decal.Angle.Degrees;
        ent.Comp.DecalSnap = args.Snap;
        ent.Comp.DecalZIndex = args.Decal.ZIndex;
        Dirty(ent);
    }

    protected bool ValidDecal(Decal decal) => Proto.HasIndex<DecalPrototype>(decal.Id)
        && ValidColor(decal.Color ?? Color.White) && double.IsFinite(decal.Angle.Theta)
        && decal.ZIndex is >= -100 and <= 100;

    private void OnColorPicked(Entity<SprayPainterComponent> ent, ref SprayPainterColorPickedMessage args)
    {
        if (ent.Comp.Mode == SprayPainterMode.Airlocks && args.Key != null
            && ent.Comp.ColorPalette.TryGetValue(args.Key, out var color))
        {
            ent.Comp.AirlockColor = color;
            Dirty(ent);
            return;
        }
        SetColor(ent, args.Key);
    }

    private void OnSpritePicked(Entity<SprayPainterComponent> ent, ref SprayPainterSpritePickedMessage args)
    {
        if (args.Index < 0 || args.Index >= Styles.Count || ent.Comp.Mode != SprayPainterMode.Airlocks)
            return;

        ent.Comp.Index = args.Index;
        Dirty(ent, ent.Comp);
    }

    private void SetColor(Entity<SprayPainterComponent> ent, string? paletteKey)
    {
        if (paletteKey == null || paletteKey == ent.Comp.PickedColor)
            return;

        if (!ent.Comp.ColorPalette.ContainsKey(paletteKey))
            return;

        ent.Comp.PickedColor = paletteKey;
        ent.Comp.PipeColor = ent.Comp.ColorPalette[paletteKey];
        Dirty(ent, ent.Comp);
    }

    #endregion

    private void OnAirlockInteract(Entity<PaintableAirlockComponent> ent, ref InteractUsingEvent args)
    {
        if (args.Handled)
            return;

        if (!TryComp<SprayPainterComponent>(args.Used, out var painter) || painter.Mode != SprayPainterMode.Airlocks
            || painter.Index < 0 || painter.Index >= Styles.Count)
            return;

        var group = Proto.Index<AirlockGroupPrototype>(ent.Comp.Group);

        var style = Styles[painter.Index];
        if (!group.StylePaths.TryGetValue(style.Name, out var sprite))
        {
            string msg = Loc.GetString("spray-painter-style-not-available");
            _popup.PopupClient(msg, args.User, args.User);
            return;
        }

        var doAfterEventArgs = new DoAfterArgs(EntityManager, args.User, painter.AirlockSprayTime, new SprayPainterDoorDoAfterEvent(sprite, style.Department, painter.AirlockColor), args.Used, target: ent, used: args.Used)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = true,
        };
        if (!DoAfter.TryStartDoAfter(doAfterEventArgs, out var id))
            return;

        args.Handled = true;

        // Log the attempt
        _adminLogger.Add(LogType.Action, LogImpact.Low, $"{ToPrettyString(args.User):user} is painting {ToPrettyString(ent):target} to '{style.Name}' at {Transform(ent).Coordinates:targetlocation}");
    }

    #region Style caching

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (!args.WasModified<AirlockGroupPrototype>() && !args.WasModified<AirlockDepartmentsPrototype>())
            return;

        Styles.Clear();
        Groups.Clear();
        CacheStyles();

        // style index might be invalid now so check them all
        var max = Styles.Count - 1;
        var query = AllEntityQuery<SprayPainterComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.Index > max)
            {
                comp.Index = max;
                Dirty(uid, comp);
            }
        }
    }

    protected virtual void CacheStyles()
    {
        // collect every style's name
        var names = new SortedSet<string>();
        foreach (var group in Proto.EnumeratePrototypes<AirlockGroupPrototype>())
        {
            Groups.Add(group);
            foreach (var style in group.StylePaths.Keys)
            {
                names.Add(style);
            }
        }

        // get their department ids too for the final style list
        var departments = Proto.Index<AirlockDepartmentsPrototype>(Departments);
        Styles.Capacity = names.Count;
        foreach (var name in names)
        {
            departments.Departments.TryGetValue(name, out var department);
            Styles.Add(new AirlockStyle(name, department));
        }
    }

    #endregion
}

public record struct AirlockStyle(string Name, string? Department);

using Content.Shared.SprayPainter;
using Content.Shared.SprayPainter.Components;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Content.Client.Decals.UI;
using Content.Shared.Decals;
using Robust.Shared.Prototypes;
using System.Numerics;

namespace Content.Client.SprayPainter.UI;

public sealed class SprayPainterBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private SprayPainterWindow? _window;
    private DecalPlacerWindow? _decalWindow;
    private SprayPainterMode _lastMode;

    public SprayPainterBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<SprayPainterWindow>();

        _window.OnSpritePicked = OnSpritePicked;
        _window.OnColorPicked = OnColorPicked;
        _window.OnCustomColorPicked = color => SendMessage(new SprayPainterCustomColorMessage(color));
        _window.OnDecalPicker = OpenDecalPicker;
        _window.OnClose += () => _decalWindow?.Close();

        UpdateFromComponent();
    }

    public void UpdateFromComponent()
    {
        if (EntMan.TryGetComponent(Owner, out SprayPainterComponent? comp))
        {
            _window?.Populate(EntMan.System<SprayPainterSystem>().Entries, comp);
            if (_lastMode != comp.Mode)
                _decalWindow?.Close();
            _lastMode = comp.Mode;
        }
    }

    private void OpenDecalPicker()
    {
        if (!EntMan.TryGetComponent<SprayPainterComponent>(Owner, out var comp) || comp.Mode != SprayPainterMode.Decals)
            return;
        _decalWindow ??= new DecalPlacerWindow();
        var selected = new Decal(Vector2.Zero, comp.DecalId, comp.DecalColor,
            Angle.FromDegrees(comp.DecalRotation), comp.DecalZIndex, true);
        _decalWindow.ConfigureForPainter(selected, comp.DecalSnap,
            (decal, snap) => SendMessage(new SprayPainterDecalSettingsMessage(decal, snap)),
            (decal, location) => SendMessage(new SprayPainterPlaceDecalMessage(EntMan.GetNetCoordinates(location), decal)),
            location => SendMessage(new SprayPainterEraseDecalMessage(EntMan.GetNetCoordinates(location))));
        _decalWindow.Populate(IoCManager.Resolve<IPrototypeManager>().EnumeratePrototypes<DecalPrototype>(), includeHidden: true);
        _decalWindow.OpenCentered();
    }

    private void OnSpritePicked(ItemList.ItemListSelectedEventArgs args)
    {
        SendMessage(new SprayPainterSpritePickedMessage(args.ItemIndex));
    }

    private void OnColorPicked(ItemList.ItemListSelectedEventArgs args)
    {
        var key = _window?.IndexToColorKey(args.ItemIndex);
        SendMessage(new SprayPainterColorPickedMessage(key));
    }
}

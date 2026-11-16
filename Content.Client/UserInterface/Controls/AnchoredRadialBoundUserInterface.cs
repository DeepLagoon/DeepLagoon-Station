using System.Numerics;
using Content.Shared._DeepLagoon.Radial;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.UserInterface.Controls;

public sealed class AnchoredRadialBoundUserInterface(EntityUid owner, Enum key) : BoundUserInterface(owner, key)
{
    private AnchoredRadialMenu? _menu;

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is not AnchoredRadialState radial ||
            !EntMan.TryGetEntity(radial.Anchor, out var anchor) ||
            !EntMan.TryGetEntity(radial.Viewer, out var viewer))
            return;

        if (_menu == null)
        {
            _menu = this.CreateWindow<AnchoredRadialMenu>();
            _menu.OnSelect += selection => SendMessage(new RadialSelection(selection));
        }

        _menu.SetChoices(radial.Choices);
        _menu.OpenAtEntity(anchor.Value, viewer.Value);
    }
}

public sealed class AnchoredRadialMenu : RadialMenu
{
    private readonly RadialContainer _choices = new()
    {
        HorizontalExpand = true,
        VerticalExpand = true,
    };

    private readonly SpriteSystem _sprites = IoCManager.Resolve<IEntityManager>().System<SpriteSystem>();
    public event Action<int>? OnSelect;

    public AnchoredRadialMenu()
    {
        CloseButtonStyleClass = "RadialMenuCloseButton";
        Resizable = false;
        AddChild(_choices);
    }

    public void SetChoices(RadialChoice[] choices)
    {
        // The ring grows with its longest list, so sectors and icons are still clickable.
        _choices.InitialRadius = Math.Max(100, choices.Length * 9);
        var radius = _choices.InitialRadius + choices.Length * 5;
        MinSize = new Vector2(Math.Max(450, radius * 3 + 10));
        _choices.RemoveAllChildren();
        foreach (var choice in choices)
        {
            var button = new RadialMenuTextureButtonWithSector
            {
                SetSize = new Vector2(80),
                ToolTip = choice.Name,
                DrawBackground = false,
                DrawSeparators = false,
            };
            button.UseSourceTexture("slice", new Vector2(80), choice.Selected);
            button.AddChild(new TextureRect
            {
                Texture = _sprites.Frame0(choice.Icon),
                HorizontalAlignment = HAlignment.Center,
                VerticalAlignment = VAlignment.Center,
                SetSize = new Vector2(64),
                Stretch = TextureRect.StretchMode.KeepAspectCentered,
                MouseFilter = MouseFilterMode.Ignore,
            });
            button.OnButtonUp += _ => OnSelect?.Invoke(choice.Id);
            _choices.AddChild(button);
        }
    }
}

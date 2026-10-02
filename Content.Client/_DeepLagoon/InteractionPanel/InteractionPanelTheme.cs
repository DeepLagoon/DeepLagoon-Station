using System.Linq;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using static Robust.Client.UserInterface.StylesheetHelpers;

namespace Content.Client._DeepLagoon.InteractionPanel;

// Локальная тема. Радиусы, отступы и цвета меняются здесь, без изменения общей темы игры.
public sealed class InteractionPanelRoundedStyle : StyleBox
{
    public Color BackgroundColor { get; set; }
    public Color? BottomColor { get; set; }
    public float Radius { get; set; } = 8;

    public InteractionPanelRoundedStyle(Color color, float radius = 8, float padding = 8)
    {
        BackgroundColor = color;
        Radius = radius;
        ContentMarginLeftOverride = ContentMarginRightOverride = padding;
        ContentMarginTopOverride = ContentMarginBottomOverride = padding;
    }

    protected override void DoDraw(DrawingHandleScreen handle, UIBox2 box, float uiScale)
    {
        var radius = Math.Min(Radius * uiScale, Math.Min(box.Width, box.Height) / 2);
        // Полосы ограничены окружностью угла; фон не выходит за скруглённый контур.
        for (var y = 0f; y < box.Height; y += 1f)
        {
            var height = Math.Min(1f, box.Height - y);
            var center = y + height / 2;
            var edge = Math.Min(center, box.Height - center);
            var inset = edge < radius ? radius - MathF.Sqrt(Math.Max(0, radius * radius - (radius - edge) * (radius - edge))) : 0;
            var t = center / box.Height;
            var bottom = BottomColor ?? BackgroundColor;
            var color = new Color(BackgroundColor.R + (bottom.R - BackgroundColor.R) * t,
                BackgroundColor.G + (bottom.G - BackgroundColor.G) * t,
                BackgroundColor.B + (bottom.B - BackgroundColor.B) * t, BackgroundColor.A + (bottom.A - BackgroundColor.A) * t);
            handle.DrawRect(new UIBox2(box.Left + inset, box.Top + y, box.Right - inset, box.Top + y + height), color);
        }
    }
}

public static class InteractionPanelTheme
{
    public static Stylesheet Create(Stylesheet? original) => new((original?.Rules ?? Array.Empty<StyleRule>()).Concat(new StyleRule[]
    {
        Element<PanelContainer>().Class("windowHeader")
            .Prop(PanelContainer.StylePropertyPanel, new InteractionPanelRoundedStyle(InteractionPanelAppearance.Header, 12, 4)),
        Element<ContainerButton>().Class(ContainerButton.StyleClassButton)
            .Prop(ContainerButton.StylePropertyStyleBox, new InteractionPanelRoundedStyle(InteractionPanelAppearance.Button, 7, 7)),
        Element<ContainerButton>().Class(ContainerButton.StyleClassButton).Pseudo("normal")
            .Prop(Control.StylePropertyModulateSelf, Color.White),
        Element<ContainerButton>().Class(ContainerButton.StyleClassButton).Pseudo("hover")
            .Prop(Control.StylePropertyModulateSelf, InteractionPanelAppearance.ButtonHoverTint),
        Element<ContainerButton>().Class(ContainerButton.StyleClassButton).Pseudo("pressed")
            .Prop(Control.StylePropertyModulateSelf, InteractionPanelAppearance.ButtonPressedTint),
        Element<ContainerButton>().Class(ContainerButton.StyleClassButton).Pseudo("disabled")
            .Prop(Control.StylePropertyModulateSelf, InteractionPanelAppearance.ButtonDisabledTint),
        Element<LineEdit>().Prop(LineEdit.StylePropertyStyleBox, new InteractionPanelRoundedStyle(InteractionPanelAppearance.Input, 7, 9)),
        Element<TabContainer>()
            .Prop(TabContainer.StylePropertyTabStyleBox, new InteractionPanelRoundedStyle(InteractionPanelAppearance.ActiveTab, 9, 12))
            .Prop(TabContainer.StylePropertyTabStyleBoxInactive, new InteractionPanelRoundedStyle(InteractionPanelAppearance.InactiveTab, 9, 12))
            .Prop(TabContainer.StylePropertyPanelStyleBox, new InteractionPanelRoundedStyle(InteractionPanelAppearance.TabPanel, 12, 0)),
    }).ToArray());
}



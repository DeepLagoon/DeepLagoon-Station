using Content.Shared._DeepLagoon.InteractionPanel;
using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._DeepLagoon.InteractionPanel;

public static class InteractionPanelCategoryColors
{
    // Палитра настраивается в InteractionPanelAppearance.cs (FriendlyButton, HugsButton и т.д.).
    // Текст остаётся белым; выбирайте достаточно тёмные цвета для читаемости.
    // Новую категорию добавьте в таблицу. Наведение и нажатие рассчитываются автоматически.
    public static readonly IReadOnlyDictionary<InteractionPanelCategory, Color> Backgrounds =
        new Dictionary<InteractionPanelCategory, Color>
        {
            [InteractionPanelCategory.Neutral] = InteractionPanelAppearance.NeutralButton,
            [InteractionPanelCategory.Erotic] = InteractionPanelAppearance.FriendlyButton,
            [InteractionPanelCategory.NonCon] = InteractionPanelAppearance.HugsButton,
            [InteractionPanelCategory.Vore] = InteractionPanelAppearance.PlayfulButton,
        };
    // Фон всей карточки: тот же цветовой диапазон, но темнее кнопки.
    public static readonly IReadOnlyDictionary<InteractionPanelCategory, Color> CardBackgrounds =
        new Dictionary<InteractionPanelCategory, Color>
        {
            [InteractionPanelCategory.Neutral] = InteractionPanelAppearance.NeutralCard,
            [InteractionPanelCategory.Erotic] = InteractionPanelAppearance.FriendlyCard,
            [InteractionPanelCategory.NonCon] = InteractionPanelAppearance.HugsCard,
            [InteractionPanelCategory.Vore] = InteractionPanelAppearance.PlayfulCard,
        };
}

public sealed class InteractionPanelCategoryButton : Button
{
    private readonly Color _color;
    private InteractionPanelRoundedStyle? _background;

    public InteractionPanelCategoryButton(InteractionPanelCategory category)
    {
        _color = InteractionPanelCategoryColors.Backgrounds.GetValueOrDefault(category, InteractionPanelAppearance.FallbackButton);
        _background = new InteractionPanelRoundedStyle(_color)
        {
            ContentMarginLeftOverride = 8,
            ContentMarginRightOverride = 8,
            ContentMarginTopOverride = 5,
            ContentMarginBottomOverride = 5,
        };
        StyleBoxOverride = _background;
        DrawModeChanged();
    }

    protected override void DrawModeChanged()
    {
        base.DrawModeChanged();
        if (_background == null) return; // базовый конструктор тоже вызывает этот метод
        var factor = DrawMode switch
        {
            DrawModeEnum.Hover => InteractionPanelAppearance.HoverBrightness,
            DrawModeEnum.Pressed => InteractionPanelAppearance.PressedBrightness,
            DrawModeEnum.Disabled => InteractionPanelAppearance.DisabledBrightness,
            _ => 1f,
        };
        _background.BackgroundColor = new Color(
            Math.Min(_color.R * factor, 1f), Math.Min(_color.G * factor, 1f),
            Math.Min(_color.B * factor, 1f), _color.A);
    }
}



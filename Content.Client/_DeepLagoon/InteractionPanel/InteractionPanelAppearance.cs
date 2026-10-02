namespace Content.Client._DeepLagoon.InteractionPanel;

// Цветовые определения: InteractionPanelColorDefines.cs. Здесь только преобразование в Color.
// #RRGGBB — непрозрачно; #RRGGBBAA — AA от 00 (прозрачно) до FF (непрозрачно).
// После изменения пересоберите клиент. Категории и кукла используют эту же палитру.
public static class InteractionPanelAppearance
{
    public static readonly Color NeutralButton = Color.FromHex(InteractionPanelColorDefines.NeutralButton);
    public static readonly Color NeutralCard = Color.FromHex(InteractionPanelColorDefines.NeutralCard);
    public static readonly Color WindowTop = Color.FromHex(InteractionPanelColorDefines.WindowTop); // Верх градиента окна
    public static readonly Color WindowBottom = Color.FromHex(InteractionPanelColorDefines.WindowBottom); // Низ градиента окна
    public static readonly Color CharacterPanel = Color.FromHex(InteractionPanelColorDefines.CharacterPanel); // Фон превью персонажа
    public static readonly Color Header = Color.FromHex(InteractionPanelColorDefines.Header); // Заголовок окна
    public static readonly Color Input = Color.FromHex(InteractionPanelColorDefines.Input); // Поля ввода
    public static readonly Color ActiveTab = Color.FromHex(InteractionPanelColorDefines.ActiveTab); // Активная вкладка
    public static readonly Color InactiveTab = Color.FromHex(InteractionPanelColorDefines.InactiveTab); // Неактивная вкладка
    public static readonly Color TabPanel = Color.FromHex(InteractionPanelColorDefines.TabPanel); // Фон содержимого вкладок
    public static readonly Color EditorCard = Color.FromHex(InteractionPanelColorDefines.EditorCard); // Подсказки и карточки списка своих действий
    public static readonly Color MessagePreview = Color.FromHex(InteractionPanelColorDefines.MessagePreview); // Предпросмотр сообщения
    public static readonly Color AccentText = Color.FromHex(InteractionPanelColorDefines.AccentText); // Акцентные подписи
    public static readonly Color MutedText = Color.FromHex(InteractionPanelColorDefines.MutedText); // Второстепенные подписи
    public static readonly Color DebugText = Color.FromHex(InteractionPanelColorDefines.DebugText); // Предупреждение Dev
    public static readonly Color FriendlyButton = Color.FromHex(InteractionPanelColorDefines.FriendlyButton); // Кнопка дружеского действия
    public static readonly Color HugsButton = Color.FromHex(InteractionPanelColorDefines.HugsButton); // Кнопка объятий
    public static readonly Color PlayfulButton = Color.FromHex(InteractionPanelColorDefines.PlayfulButton); // Кнопка шуточного действия
    public static readonly Color FriendlyCard = Color.FromHex(InteractionPanelColorDefines.FriendlyCard); // Карточка дружеского действия
    public static readonly Color HugsCard = Color.FromHex(InteractionPanelColorDefines.HugsCard); // Карточка объятий
    public static readonly Color PlayfulCard = Color.FromHex(InteractionPanelColorDefines.PlayfulCard); // Карточка шуточного действия
    public static readonly Color FallbackButton = Color.FromHex(InteractionPanelColorDefines.FallbackButton); // Запасной цвет категории
    public static readonly Color BodyHover = Color.FromHex(InteractionPanelColorDefines.BodyHover); // Кукла: наведение
    public static readonly Color BodySelected = Color.FromHex(InteractionPanelColorDefines.BodySelected); // Кукла: выбранная область
    public static readonly Color BodyLight = Color.FromHex(InteractionPanelColorDefines.BodyLight); // Кукла: основные части
    public static readonly Color BodyDark = Color.FromHex(InteractionPanelColorDefines.BodyDark); // Кукла: плечи и руки
    public static readonly Color BodyPelvis = Color.FromHex(InteractionPanelColorDefines.BodyPelvis); // Кукла: таз

    // Обычные кнопки: цвет основы и множители состояния (белый = без изменения).
    public static readonly Color Button = Color.FromHex(InteractionPanelColorDefines.Button);
    public static readonly Color ButtonHoverTint = new(InteractionPanelColorDefines.ButtonHoverBrightness, InteractionPanelColorDefines.ButtonHoverBrightness, InteractionPanelColorDefines.ButtonHoverBrightness);
    public static readonly Color ButtonPressedTint = new(InteractionPanelColorDefines.ButtonPressedBrightness, InteractionPanelColorDefines.ButtonPressedBrightness, InteractionPanelColorDefines.ButtonPressedBrightness);
    public static readonly Color ButtonDisabledTint = new(InteractionPanelColorDefines.ButtonDisabledBrightness, InteractionPanelColorDefines.ButtonDisabledBrightness, InteractionPanelColorDefines.ButtonDisabledBrightness);
    // Яркость цветных кнопок действий.
    public const float HoverBrightness = InteractionPanelColorDefines.ActionHoverBrightness;
    public const float PressedBrightness = InteractionPanelColorDefines.ActionPressedBrightness;
    public const float DisabledBrightness = InteractionPanelColorDefines.ActionDisabledBrightness;
}


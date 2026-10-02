namespace Content.Client._DeepLagoon.InteractionPanel;

// DEFINE цветов интерфейса. В C# #define не хранит значения, поэтому используются const string.
// Меняйте HEX здесь; InteractionPanelAppearance преобразует константы в Color.
public static class InteractionPanelColorDefines
{
    public const string NeutralButton = "#586187"; // Нейтральные действия: приглушённая лаванда
    public const string NeutralCard = "#39415C";
    public const string WindowTop = "#392344FA"; // Верх градиента окна
    public const string WindowBottom = "#21182FFA"; // Низ градиента окна
    public const string CharacterPanel = "#523967F5"; // Фон превью персонажа
    public const string Header = "#302039"; // Заголовок окна
    public const string Input = "#352740"; // Поля ввода
    public const string ActiveTab = "#80517D"; // Активная вкладка
    public const string InactiveTab = "#44304F"; // Неактивная вкладка
    public const string TabPanel = "#281D35F5"; // Фон содержимого вкладок
    public const string EditorCard = "#44304F"; // Подсказки и карточки списка своих действий
    public const string MessagePreview = "#21192C"; // Предпросмотр сообщения
    public const string AccentText = "#F1BCE6"; // Акцентные подписи
    public const string MutedText = "#C9B6D2"; // Второстепенные подписи
    public const string DebugText = "#E9C880"; // Предупреждение Dev
    public const string FriendlyButton = "#66518B"; // Кнопка дружеского действия
    public const string HugsButton = "#8A4F76"; // Кнопка объятий
    public const string PlayfulButton = "#79508E"; // Кнопка шуточного действия
    public const string FriendlyCard = "#45365E"; // Карточка дружеского действия
    public const string HugsCard = "#5C354F"; // Карточка объятий
    public const string PlayfulCard = "#50365F"; // Карточка шуточного действия
    public const string FallbackButton = "#624A78"; // Запасной цвет категории
    public const string BodyHover = "#F9CDEF"; // Кукла: наведение
    public const string BodySelected = "#DE9CCE"; // Кукла: выбранная область
    public const string BodyLight = "#AE91C4"; // Кукла: основные части
    public const string BodyDark = "#9577AD"; // Кукла: плечи и руки
    public const string BodyPelvis = "#826799"; // Кукла: таз
    public const string Button = "#58416C";
    public const float ButtonHoverBrightness = 1.2f;
    public const float ButtonPressedBrightness = 0.8f;
    public const float ButtonDisabledBrightness = 0.45f;
    public const float ActionHoverBrightness = 1.25f;
    public const float ActionPressedBrightness = 0.8f;
    public const float ActionDisabledBrightness = 0.45f;
}


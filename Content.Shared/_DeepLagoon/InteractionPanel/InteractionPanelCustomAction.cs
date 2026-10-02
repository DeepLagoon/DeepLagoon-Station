using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.InteractionPanel;

[Serializable, NetSerializable]
public sealed record InteractionPanelCustomAction
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Template { get; init; } = "$you мило поглаживает $target";
    public InteractionPanelCategory Category { get; init; }
    public string Body { get; init; } = "all";
    public InteractionPanelActionFlags Flags { get; init; } = InteractionPanelActionFlags.INTERACTION_OTHER | InteractionPanelActionFlags.INTERACTION_HUMANOID;
    public string? Sound { get; init; }
    public string ChatColor { get; init; } = "#C6E8DA";

    public InteractionPanelActionDefinition Definition() => new(Id, Body, Category, Flags, Sound,
        ChatColor: ChatColor, Title: Title, Template: Template);
}

public static class InteractionPanelCustomRules
{
    public const int MaxActions = 32;
    public const int MaxTitle = 48;
    public const int MaxTemplate = 280;
    public static readonly string[] Bodies = { "all", "head", "hands", "shoulders", "torso", "legs", "groin" };
    private static readonly Regex Tokens = new(@"\$(you|target)\b", RegexOptions.Compiled);
    // UnicodeCategory недоступен контенту в sandbox Robust. Regex проверяет те же
    // категории без обращения к запрещённому типу (управляющие/форматирующие символы).
    private static readonly Regex HiddenCharacters = new(@"[\p{Cc}\p{Cf}]", RegexOptions.Compiled);

    // Один проход: имена, содержащие $you/$target, не становятся новыми подстановками.
    public static string Render(string template, string you, string target) =>
        Tokens.Replace(template, match => match.Value == "$you" ? you : target);

    public static bool Valid(InteractionPanelCustomAction action) => ValidationError(action) == null;

    // Одна проверка для сервера и редактора: подсказка всегда соответствует реальной причине отказа.
    public static string? ValidationError(InteractionPanelCustomAction action)
    {
        if (action == null || action.Id == null) return "dl-interaction-panel-library-invalid";
        if (string.IsNullOrWhiteSpace(action.Title)) return "dl-interaction-panel-validation-title-required";
        if (action.Title.Length > MaxTitle) return "dl-interaction-panel-validation-title-long";
        if (string.IsNullOrWhiteSpace(action.Template) || !action.Template.StartsWith("$you ", StringComparison.Ordinal))
            return "dl-interaction-panel-validation-template-start";
        if (action.Template.Length > MaxTemplate) return "dl-interaction-panel-validation-template-long";
        if (Tokens.Replace(action.Template, "").Contains('$')) return "dl-interaction-panel-validation-tokens";
        if (HiddenCharacters.IsMatch(action.Title) || HiddenCharacters.IsMatch(action.Template))
            return "dl-interaction-panel-validation-hidden";
        if (!Enum.IsDefined(action.Category) || !Bodies.Contains(action.Body)) return "dl-interaction-panel-library-invalid";
        const InteractionPanelActionFlags targets = InteractionPanelActionFlags.INTERACTION_HUMANOID | InteractionPanelActionFlags.INTERACTION_NON_HUMANOID;
        const InteractionPanelActionFlags actors = InteractionPanelActionFlags.INTERACTION_SELF | InteractionPanelActionFlags.INTERACTION_OTHER;
        if ((action.Flags & targets) == 0) return "dl-interaction-panel-validation-targets";
        if ((action.Flags & actors) == 0) return "dl-interaction-panel-validation-actors";
        if ((action.Flags & ~(targets | actors)) != 0) return "dl-interaction-panel-library-invalid";
        if (action.ChatColor == null || action.ChatColor.Length != 7 || action.ChatColor[0] != '#' ||
            !int.TryParse(action.ChatColor.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            return "dl-interaction-panel-validation-color";
        return null;
    }
}

// Библиотека общая для стандартных и пользовательских действий.
// Добавление звука: новый YAML interactionPanelSound в Resources/Prototypes/_DeepLagoon,
// уникальный id, name/category (ключи локализации), path (/Audio/...ogg).
// Игрок выбирает только id; произвольные пути/URL сервер не принимает.
[Prototype("interactionPanelSound")]
public sealed partial class InteractionPanelSoundPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string Name = default!;
    [DataField(required: true)] public string Category = default!;
    [DataField] public string? Path;
    // Набор файлов для одного пункта библиотеки (доступен и в игровом конструкторе).
    [DataField] public string[]? Paths;
    // Громкость в дБ: 0 — исходная, -5 — тише. Общая для действий с этим звуком.
    [DataField] public float Volume = -5f;
}

[Serializable, NetSerializable]
public sealed class InteractionPanelLibraryRequestEvent : EntityEventArgs;

[Serializable, NetSerializable]
public sealed class InteractionPanelLibraryEvent(InteractionPanelCustomAction[] actions, string[] available, string message = "") : EntityEventArgs
{
    public InteractionPanelCustomAction[] Actions = actions;
    public string[] Available = available;
    public string Message = message;
}

[Serializable, NetSerializable]
public sealed class InteractionPanelCustomSaveEvent(InteractionPanelCustomAction action) : EntityEventArgs
{
    public InteractionPanelCustomAction Action = action;
}

[Serializable, NetSerializable]
public sealed class InteractionPanelCustomDeleteEvent(string id) : EntityEventArgs
{
    public string Id = id;
}



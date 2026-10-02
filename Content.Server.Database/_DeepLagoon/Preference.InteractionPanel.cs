using System.ComponentModel.DataAnnotations.Schema;

namespace Content.Server.Database;

public partial class Preference
{
    // Библиотека привязана к аккаунту, а не персонажу/раунду. JSON содержит только валидированные шаблоны.
    // Имя колонки сохранено для совместимости с уже созданными БД.
    [Column("social_actions_json")]
    public string InteractionPanelActionsJson { get; set; } = "[]";
}

using System.ComponentModel.DataAnnotations.Schema;

namespace Content.Server.Database;

public partial class Profile
{
    // 0 = No, 1 = Ask, 2 = Yes. Старые профили после миграции получают Ask.
    // Имена свойств можно менять, но имена колонок сохраняем для существующих БД.
    // Переименование колонки требует отдельной миграции, а не правки старой.
    [Column("friendly_consent")]
    public int ERPConsent { get; set; } = 1;
    [Column("hugs_consent")]
    public int NonConConsent { get; set; } = 1;
    [Column("playful_consent")]
    public int VoreConsent { get; set; } = 1;
}

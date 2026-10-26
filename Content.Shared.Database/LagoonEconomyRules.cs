namespace Content.Shared.Database;

public static class LagoonEconomyRules
{
    public const int PassDays = 30;
    public const int DailyReward = 3;
    public const int ReadyReward = 5;
    public const int ObjectiveReward = 2;
    public static readonly TimeSpan DailyActivity = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ReadyActivity = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan PassDuration = TimeSpan.FromDays(PassDays);
    public static int ProductTier(int gameTier, bool spark, bool discordBooster = false) => Math.Max(discordBooster ? 2 : 0, gameTier > 0 ? Math.Clamp(gameTier, 1, 3) + 1 : spark ? 1 : 0);
    public static int GameTier(int productTier) => Math.Clamp(productTier - 1, 0, 3);
    public static int HourlyRate(int productTier) => productTier switch { 2 => 4, 3 => 5, 4 => 6, _ => 2 };
    public static int PassPrice(int productTier) => productTier switch { 1 => 25, 2 => 600, 3 => 1500, 4 => 6000, _ => throw new ArgumentOutOfRangeException(nameof(productTier)) };
    public static int PaidGift(int productTier) => productTier switch { 1 => 0, 2 => 100, 3 => 250, 4 => 1000, _ => throw new ArgumentOutOfRangeException(nameof(productTier)) };
    public static int DecorDiscount(int productTier) => productTier switch { 2 => 5, 3 => 10, 4 => 15, _ => 0 };
    public static long DecorPrice(long price, int productTier) => checked((price * (100 - DecorDiscount(productTier)) + 99) / 100);
    public static string Name(int productTier) => productTier switch { 1 => "Искра", 2 => "Импульс", 3 => "Сияние", 4 => "Сверхновая", _ => "Без подписки" };
    public static string MoscowDay(DateTime utc) => utc.ToUniversalTime().AddHours(3).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

using System.Globalization;
using System.Text.RegularExpressions;
using Content.Server.Database;

namespace Content.Server.Discord;

public static class LagoonCoinWebhookFormatter
{
    private static readonly Regex DiscordActorPrefix = new(@"^Discord actor=[0-9]{15,20}; ", RegexOptions.CultureInvariant);
    public static WebhookPayload Format(LagoonCoinOperation operation, string server, string player, string actor)
    {
        var purchase = operation.OperationId.StartsWith("purchase:", StringComparison.Ordinal);
        var spending = operation.Amount < 0 && operation.OperationId.StartsWith("spend:", StringComparison.Ordinal);
        var transfer = operation.OperationId.StartsWith(AccountCurrencyOperations.TransferOperationPrefix, StringComparison.Ordinal);
        var title = transfer ? "Перевод Lagoon Coin"
            : spending ? "Трата Lagoon Coin"
            : operation.Amount < 0 ? "Снятие Lagoon Coin"
            : purchase ? "Покупка Lagoon Coin"
            : "Начисление Lagoon Coin";
        var source = transfer ? "Перевод между игроками"
            : spending ? "Магазин / покупка за LC"
            : purchase ? "Подтверждённый платёж"
            : operation.Reason switch
        {
            "ready" => "Старт раунда через Ready",
            "playtime" => "Игровое время",
            "antagonist-objective" => "Цель антагониста",
            _ => operation.OperationId.StartsWith("discord:", StringComparison.Ordinal) ? "Команда Discord" : "Администрация / сервер"
        };
        return new WebhookPayload
        {
            Username = "DeepLagoon · Lagoon Coin",
            AllowedMentions = new WebhookMentions(),
            Embeds = new List<WebhookEmbed>
            {
                new()
                {
                    Title = title,
                    Color = operation.Amount < 0 ? 0xED4245 : purchase ? 0xFEE75C : 0x57F287,
                    Description = DiscordActorPrefix.Replace(operation.Reason, ""),
                    Fields = new List<WebhookEmbedField>
                    {
                        new() { Name = "Игрок", Value = Limit(player, 128) },
                        new() { Name = "Изменение", Value = operation.Amount.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " LC", Inline = true },
                        new() { Name = "Баланс после операции", Value = operation.BalanceAfter.ToString(CultureInfo.InvariantCulture) + " LC", Inline = true },
                        new() { Name = "Источник", Value = source },
                        new() { Name = "Автор", Value = operation.ActorId == null && DiscordActorPrefix.IsMatch(operation.Reason)
                            ? "Администрация Discord" : Limit(actor, 128) },
                        new() { Name = "Операция", Value = operation.OperationId }
                    },
                    Footer = new WebhookEmbedFooter { Text = $"{Limit(server, 160)} · {DateTime.SpecifyKind(operation.CreatedAt, DateTimeKind.Utc):yyyy-MM-dd HH:mm:ss} UTC" }
                }
            }
        };
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";
}

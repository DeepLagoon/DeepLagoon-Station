using System.Text;
using Content.Server.Database;
using Content.Shared.Administration.Notes;
using Content.Shared.Database;

namespace Content.Server.Discord;

public static class ModerationWebhookFormatter
{
    public static bool ShouldNotify(ModerationEvent change)
        => !change.Secret && change.Type != NoteType.Watchlist &&
           change.Action is not ("Удалено" or "Скрыто из заметок");

    public static List<WebhookPayload> Format(ModerationEvent change, string server, string player, string actor)
    {
        if (!ShouldNotify(change))
            return new List<WebhookPayload>();
        var type = change.Type switch
        {
            NoteType.ServerBan => "Бан сервера",
            NoteType.RoleBan => "Бан роли / профессии",
            NoteType.Note => "Заметка",
            NoteType.Watchlist => "Watchlist",
            NoteType.Message => "Сообщение игроку",
            _ => change.Type.ToString()
        };
        var text = new StringBuilder("**Причина / текст**\n").Append(change.Message);
        if (change.PreviousMessage != null && !change.PreviousSecret)
            text.Append("\n\n**Предыдущий текст**\n").Append(change.PreviousMessage);
        var fields = new List<WebhookEmbedField>
        {
            new() { Name = "Игрок", Value = Limit(player, 512) },
            new() { Name = "Автор действия", Value = Limit(actor, 512) },
            new() { Name = "Запись", Value = $"{change.Type} #{change.Id}", Inline = true },
            new() { Name = "Раунд", Value = change.Round?.ToString() ?? "—", Inline = true },
            new() { Name = "Тяжесть", Value = change.Severity?.ToString() ?? "—", Inline = true },
            new() { Name = "Срок", Value = change.Expires?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "Бессрочно" },
            new() { Name = "Скрытая заметка", Value = change.Secret ? "Да" : "Нет", Inline = true }
        };
        if (change.Roles is { Length: > 0 })
            fields.Add(new WebhookEmbedField { Name = "Роли", Value = Limit(string.Join(", ", change.Roles), 512) });
        var payloads = new List<WebhookPayload>();
        var remaining = text.ToString();
        while (remaining.Length > 0)
        {
            var length = Math.Min(3000, remaining.Length);
            if (length < remaining.Length && char.IsHighSurrogate(remaining[length - 1]))
                length--;
            payloads.Add(new WebhookPayload
            {
                AllowedMentions = new WebhookMentions(),
                Embeds = new List<WebhookEmbed>
                {
                    new()
                    {
                        Title = $"{type}: {change.Action}",
                        Description = remaining[..length],
                        Color = change.Action == "Снят" ? 0x57F287 : 0xED4245,
                        Fields = fields,
                        Footer = new WebhookEmbedFooter { Text = $"{Limit(server, 160)} · {change.Time.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC · часть {payloads.Count + 1}" }
                    }
                }
            });
            remaining = remaining[length..];
        }
        return payloads;
    }

    private static string Limit(string text, int limit) => text.Length <= limit ? text : text[..(limit - 1)] + "…";
}

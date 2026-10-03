using Content.Server.EUI;
using Content.Shared._DeepLagoon.DiscordLink;
using Content.Shared.Eui;

namespace Content.Server._DeepLagoon.DiscordLink;

public sealed class DiscordLinkEui(DiscordLinkStore store, Func<bool> admitted) : BaseEui
{
    private string _message = "Откройте канал привязки Discord, нажмите «привязать дискорд» и создайте тикет.\nЗатем нажмите здесь «Создать код» и введите его в тикете: /link_discord code:КОД.\nКод подтверждает владение вашим аккаунтом. Не отправляйте его другим людям.";
    private string _code = "";
    private bool _linked;

    public override void Opened()
    {
        _linked = store.IsLinked(Player.UserId.UserId);
        if (_linked)
            _message = "Discord уже привязан. Дождитесь одобрения WL-заявки. До допуска лобби и игра недоступны.";
        StateDirty();
    }
    public override EuiStateBase GetNewState() => new DiscordLinkEuiState(_message, _code, _linked);

    public override void HandleMessage(EuiMessageBase msg)
    {
        if (msg is CloseEuiMessage && !admitted())
        {
            StateDirty();
            return;
        }
        base.HandleMessage(msg);
        if (IsShutDown || msg is not (GenerateDiscordLinkCode or CheckDiscordLink))
            return;
        _linked = store.IsLinked(Player.UserId.UserId);
        if (_linked)
        {
            _code = "";
            _message = "Discord успешно привязан. Теперь создайте WL-заявку в Discord. После одобрения регистраторами откроется доступ к лобби и игре. До этого проверка обязательна.";
        }
        else if (msg is GenerateDiscordLinkCode)
        {
            try
            {
                // The UID comes exclusively from the authenticated EUI session.
                _code = store.Issue(Player.UserId.UserId, Player.Name);
                _message = "Введите этот код в своём тикете Discord командой /link_discord.\nКод действует 10 минут. Создание нового кода отменяет предыдущий.";
            }
            catch (DiscordLinkStore.LinkException)
            {
                _message = "Повторный код можно получить через 30 секунд. Текущий код остаётся действительным.";
            }
        }
        else
            _message = "Привязка пока не подтверждена. Введите код в своём тикете через /link_discord.";
        StateDirty();
    }
}

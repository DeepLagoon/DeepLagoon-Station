namespace Content.Client._DeepLagoon.WebUI;

public sealed partial class NativeInterfacePanel
{
    private void Economy()
    {
        if (_data.Bool("loading")) { Text(_body, "Загрузка Lagoon Coin…"); return; }
        if (!string.IsNullOrEmpty(_data.String("error")))
        {
            Text(_body, _data.String("error"));
            Button(_body, "Повторить загрузку", () => Send("retry"));
            return;
        }
        Text(_body, "Баланс: " + _data.String("balance") + " LC");
        Text(_body, _data.String("message"));
        if (_data.Int("passTier") > 0) Text(_body, "Доступ за LC: осталось " + _data.String("passDays") + " дней" + (_data.Bool("paused") ? " · приостановлен" : ""));
        Text(_body, "Подписки за LC открывают игровые бонусы на 30 дней, без подарка за рублёвую оплату.");
        foreach (var item in _data.Array("subscriptions"))
        {
            var tier = item.String("id");
            var product = new TguiData().String("product", tier).String("order", Guid.NewGuid().ToString());
            var button = Button(_body, item.String("name") + " · " + item.String("price") + " LC", () => { });
            var confirmed = false;
            button.OnPressed += _ =>
            {
                if (confirmed) { button.Disabled = true; Send("pass", product); }
                else { confirmed = true; button.Text = "Подтвердить: " + item.String("name"); }
            };
        }
        var color = Field(_body, "Цвет никнейма OOC (#RRGGBB)", _data.String("color"));
        Button(_body, "Сохранить цвет", () => Send("color", new TguiData().String("product", color.Text).String("order", Guid.NewGuid().ToString())), _data.Int("tier") > 0);
        Text(_body, "Ваш реферальный код: " + _data.String("referralCode"));
        var referral = Field(_body, "Код пригласившего");
        Button(_body, "Закрепить код", () => Send("referral", new TguiData().String("product", referral.Text).String("order", Guid.NewGuid().ToString())));
    }
}

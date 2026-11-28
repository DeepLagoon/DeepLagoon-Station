using System.Globalization;
using System.Linq;
using Content.Client.Stylesheets;
using Content.Shared._DeepLagoon.Exchange;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;

namespace Content.Client._DeepLagoon.Exchange;

public sealed partial class ExchangeView
{
    private Label _walletHeading = default!, _loginHeading = default!, _transferPreview = default!, _portfolioValue = default!, _walletOwner = default!, _transferAvailable = default!;
    private LineEdit _loginAddress = default!, _recipient = default!, _transferQuantity = default!, _addressDisplay = default!;
    private PasswordField _password = default!, _repeatPassword = default!, _currentPassword = default!, _newPassword = default!, _confirmPassword = default!;
    private OptionButton _ownedWallets = default!, _transferCurrency = default!;
    private BoxContainer _walletFunds = default!, _transferForm = default!, _loginForm = default!, _withdrawals = default!;
    private Button _login = default!, _logout = default!, _newWallet = default!, _confirmTransfer = default!, _previewTransfer = default!, _changePassword = default!, _sendCoins = default!, _tradeCoins = default!, _cancelLogin = default!;
    private TabContainer _walletTabs = default!, _walletSections = default!;
    private bool _creatingWallet, _showLogin = true, _loginSubmitted, _lastUnlocked, _hideBalances;
    private string _lastWallet = "";
    private static readonly Color WalletMuted = Color.FromHex("#94ADBB");

    private static PanelContainer WalletCard(Control child, string colour = "#182C36")
    {
        var panel = new PanelContainer { HorizontalExpand = true, PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex(colour) } };
        child.Margin = new Thickness(12);
        panel.AddChild(child);
        return panel;
    }
    private static Label WalletLabel(string text, bool heading = false)
    {
        var label = new Label { Text = text, ClipText = true, HorizontalExpand = true };
        if (heading) label.AddStyleClass(StyleNano.StyleClassLabelHeading);
        return label;
    }

    private void InitializeWallet(TabContainer tabs)
    {
        _walletTabs = tabs;
        var wallet = Column();
        tabs.AddChild(wallet);
        tabs.SetTabTitle(2, L("wallet"));
        var hero = Column(); hero.Margin = new Thickness(0);
        var top = Row();
        _walletHeading = WalletLabel(L("wallet-locked"), true);
        top.AddChild(_walletHeading);
        var hide = new Button { Text = L("hide-balances"), ToggleMode = true };
        hide.OnPressed += _ => { _hideBalances = hide.Pressed; hide.Text = L(_hideBalances ? "show-balances" : "hide-balances"); RenderWallet(); };
        top.AddChild(hide); hero.AddChild(top);
        hero.AddChild(new Label { Text = L("portfolio-estimate"), FontColorOverride = WalletMuted });
        _portfolioValue = WalletLabel("—", true); _portfolioValue.FontColorOverride = Colours[0];
        hero.AddChild(_portfolioValue);
        var address = Row();
        address.AddChild(new Label { Text = L("address"), FontColorOverride = WalletMuted });
        _addressDisplay = new LineEdit { Editable = false, HorizontalExpand = true, Name = "WalletAddress" };
        address.AddChild(_addressDisplay);
        var copy = new Button { Text = L("copy-address") };
        copy.OnPressed += _ => CopyAddress(); address.AddChild(copy); hero.AddChild(address);
        _walletOwner = WalletLabel(""); _walletOwner.FontColorOverride = WalletMuted; hero.AddChild(_walletOwner);
        var quick = Row();
        _sendCoins = new Button { Text = L("send-coins"), HorizontalExpand = true, Modulate = Colours[0] };
        _sendCoins.OnPressed += _ => _walletSections.CurrentTab = 1;
        var receive = new Button { Text = L("receive-coins"), HorizontalExpand = true };
        receive.OnPressed += _ => CopyAddress();
        _tradeCoins = new Button { Text = L("trade-coins"), HorizontalExpand = true };
        _tradeCoins.OnPressed += _ => _walletTabs.CurrentTab = 0;
        quick.AddChild(_sendCoins); quick.AddChild(receive); quick.AddChild(_tradeCoins); hero.AddChild(quick);
        wallet.AddChild(WalletCard(hero, "#13353C"));
        var accounts = Row();
        _ownedWallets = new OptionButton { HorizontalExpand = true };
        _ownedWallets.OnItemSelected += e =>
        {
            _ownedWallets.SelectId(e.Id);
            if (_snapshot?.Wallets.ElementAtOrDefault(e.Id) is not { } item) return;
            _loginAddress.Text = item.Address; _creatingWallet = false; _showLogin = true;
            ClearPasswords(); RenderLogin();
        };
        accounts.AddChild(_ownedWallets);
        var switchWallet = new Button { Text = L("switch-wallet") };
        switchWallet.OnPressed += _ => { _showLogin = !_showLogin; _creatingWallet = false; ClearPasswords(); RenderLogin(); };
        accounts.AddChild(switchWallet);
        _newWallet = new Button { Text = "+", ToolTip = L("new-wallet") };
        _newWallet.OnPressed += _ => { _creatingWallet = true; _showLogin = true; ClearPasswords(); RenderLogin(); };
        accounts.AddChild(_newWallet); wallet.AddChild(accounts);
        _loginForm = Column();
        _loginHeading = WalletLabel(L("login"), true); _loginForm.AddChild(_loginHeading);
        _loginAddress = new LineEdit { PlaceHolder = L("address"), HorizontalExpand = true, Name = "WalletLoginAddress",
            IsValid = text => text.Length <= 6 && text.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9') };
        _loginAddress.OnTextChanged += _ => RenderLogin(); _loginForm.AddChild(_loginAddress);
        _password = new PasswordField { PlaceHolder = L("password"), HorizontalExpand = true, Name = "WalletLoginPassword" };
        _repeatPassword = new PasswordField { PlaceHolder = L("password-repeat"), HorizontalExpand = true, Name = "WalletRepeatPassword" };
        _loginForm.AddChild(_password); _loginForm.AddChild(_repeatPassword);
        var loginActions = Row();
        _login = new Button { Text = L("login"), HorizontalExpand = true };
        _login.OnPressed += _ => Login();
        _password.OnTextEntered += _ => Login(); _repeatPassword.OnTextEntered += _ => Login();
        _cancelLogin = new Button { Text = L("cancel") };
        _cancelLogin.OnPressed += _ => { _showLogin = false; _creatingWallet = false; ClearPasswords(); RenderLogin(); };
        loginActions.AddChild(_login); loginActions.AddChild(_cancelLogin); _loginForm.AddChild(loginActions);
        wallet.AddChild(WalletCard(_loginForm));
        _walletSections = new TabContainer { HorizontalExpand = true };
        wallet.AddChild(_walletSections);
        var assets = Column(); _walletSections.AddChild(assets); _walletSections.SetTabTitle(0, L("assets"));
        _walletFunds = Row(); assets.AddChild(_walletFunds);
        _transferForm = Column(); _walletSections.AddChild(_transferForm); _walletSections.SetTabTitle(1, L("send-coins"));
        _transferForm.AddChild(WalletLabel(L("transfer"), true));
        _transferAvailable = WalletLabel(""); _transferAvailable.FontColorOverride = WalletMuted; _transferForm.AddChild(_transferAvailable);
        _recipient = new LineEdit { PlaceHolder = L("recipient-address"), HorizontalExpand = true,
            IsValid = text => text.Length <= 6 && text.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9') };
        _recipient.OnTextChanged += _ => InvalidateTransfer(); _transferForm.AddChild(_recipient);
        var amount = Row();
        _transferCurrency = new OptionButton { MinWidth = 95 };
        foreach (var symbol in new[] { "CTB", "IDEA", "GOI" }) _transferCurrency.AddItem(symbol);
        _transferCurrency.OnItemSelected += e => { _transferCurrency.SelectId(e.Id); InvalidateTransfer(); };
        _transferQuantity = new LineEdit { Text = "1", HorizontalExpand = true };
        _transferQuantity.OnTextChanged += _ => InvalidateTransfer();
        amount.AddChild(_transferCurrency); amount.AddChild(_transferQuantity);
        var max = new Button { Text = "MAX" };
        max.OnPressed += _ => { var available = _snapshot?.Assets.FirstOrDefault(a => a.Id == Assets[_transferCurrency.SelectedId])?.WalletUnits ?? 0;
            _transferQuantity.Text = Coins(Math.Min(1000000, available)); InvalidateTransfer(); };
        amount.AddChild(max); _transferForm.AddChild(amount);
        _transferPreview = WalletLabel(""); _transferPreview.FontColorOverride = Colours[0];
        _transferForm.AddChild(WalletCard(_transferPreview, "#123139"));
        var actions = Row();
        _previewTransfer = new Button { Text = L("check-transfer"), HorizontalExpand = true };
        _previewTransfer.OnPressed += _ => Transfer(false);
        _confirmTransfer = new Button { Text = L("confirm-transfer"), Disabled = true, HorizontalExpand = true, Modulate = Colours[0] };
        _confirmTransfer.OnPressed += _ => Transfer(true);
        actions.AddChild(_previewTransfer); actions.AddChild(_confirmTransfer); _transferForm.AddChild(actions);
        var history = Column(); _walletSections.AddChild(history); _walletSections.SetTabTitle(2, L("history"));
        history.AddChild(WalletLabel(L("withdrawal-history"), true));
        var historyScroll = new ScrollContainer { MinHeight = 150, MaxHeight = 260, HorizontalExpand = true };
        _withdrawals = Column(); historyScroll.AddChild(_withdrawals); history.AddChild(historyScroll);
        var security = Column(); _walletSections.AddChild(security); _walletSections.SetTabTitle(3, L("security"));
        security.AddChild(WalletLabel(L("change-password"), true));
        _currentPassword = new PasswordField { PlaceHolder = L("current-password"), HorizontalExpand = true, Name = "WalletCurrentPassword" };
        _newPassword = new PasswordField { PlaceHolder = L("new-password"), HorizontalExpand = true, Name = "WalletNewPassword" };
        _confirmPassword = new PasswordField { PlaceHolder = L("password-repeat"), HorizontalExpand = true, Name = "WalletConfirmPassword" };
        security.AddChild(_currentPassword); security.AddChild(_newPassword); security.AddChild(_confirmPassword);
        _changePassword = new Button { Text = L("change-password"), Name = "WalletChangePassword", HorizontalExpand = true };
        _changePassword.OnPressed += _ => ChangePassword(); _confirmPassword.OnTextEntered += _ => ChangePassword();
        security.AddChild(_changePassword);
        _logout = new Button { Text = L("logout"), HorizontalExpand = true };
        _logout.OnPressed += _ => { ClearPasswords(); WalletAction(ExchangeAction.Logout); };
        security.AddChild(_logout);
    }

    private void CopyAddress()
    {
        if (_snapshot?.Address is not { Length: > 0 } address) return;
        IoCManager.Resolve<IClipboardManager>().SetText(address);
        _status.Text = L("address-copied");
    }
    private void ClearPasswords()
    {
        _password.ClearSecret(); _repeatPassword.ClearSecret();
        _currentPassword.ClearSecret(); _newPassword.ClearSecret(); _confirmPassword.ClearSecret();
    }
    private bool SettingPassword() => _creatingWallet || _snapshot?.Wallets.Any(w =>
        w.Address.Equals(_loginAddress.Text.Trim(), StringComparison.OrdinalIgnoreCase) && w.NeedsPassword) == true;
    private void RenderLogin()
    {
        var setup = SettingPassword();
        _loginForm.Parent!.Visible = _showLogin || _snapshot?.WalletUnlocked != true;
        _loginHeading.Text = L(_creatingWallet ? "new-wallet" : setup ? "set-password" : "login");
        _repeatPassword.Visible = setup; _loginAddress.Visible = !_creatingWallet;
        _cancelLogin.Visible = _snapshot?.WalletUnlocked == true;
        _login.Text = L(_creatingWallet ? "create-wallet" : setup ? "set-password" : "login");
    }
    private void WalletAction(ExchangeAction action, string address = "", string password = "", string newPassword = "")
    {
        if (Units() == 0) _quantity.Text = "1";
        Request?.Invoke(action, _selected, Units(), Guid.Empty, _period, address, password, newPassword);
    }
    private void Login()
    {
        if (_password.Secret.Length is < 6 or > 128) { _status.Text = L("error-password_length"); return; }
        if (SettingPassword() && _password.Secret != _repeatPassword.Secret) { _status.Text = L("password-mismatch"); return; }
        var secret = _password.Secret;
        _password.ClearSecret(); _repeatPassword.ClearSecret();
        _status.Text = L("processing"); _loginSubmitted = true;
        WalletAction(_creatingWallet ? ExchangeAction.CreateWallet : ExchangeAction.Login, _loginAddress.Text.Trim().ToUpperInvariant(), secret);
    }
    private void ChangePassword()
    {
        if (_snapshot?.WalletUnlocked != true) return;
        if (_currentPassword.Secret.Length is < 6 or > 128 || _newPassword.Secret.Length is < 6 or > 128)
        { _status.Text = L("error-password_length"); return; }
        if (_newPassword.Secret != _confirmPassword.Secret) { _status.Text = L("password-mismatch"); return; }
        var current = _currentPassword.Secret; var replacement = _newPassword.Secret;
        ClearPasswords(); _changePassword.Disabled = true; _status.Text = L("processing");
        WalletAction(ExchangeAction.ChangePassword, _snapshot.Address, current, replacement);
    }
    private static long ParseUnits(string text) => decimal.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint,
        Invariant, out var n) && n is > 0 and <= 1000 && n * 1000 == decimal.Truncate(n * 1000) ? (long)(n * 1000) : 0;
    private void InvalidateTransfer()
    {
        _confirmTransfer.Disabled = true; _transferPreview.Text = L("transfer-review");
        UpdateTransferAvailability();
    }
    private void UpdateTransferAvailability()
    {
        var asset = _snapshot?.Assets.FirstOrDefault(a => a.Id == Assets[_transferCurrency.SelectedId]);
        _transferAvailable.Text = L("available") + ": " + (asset == null ? "—" : Coins(asset.WalletUnits) + " " + asset.Symbol);
        _previewTransfer.Disabled = _snapshot?.WalletUnlocked != true || ParseUnits(_transferQuantity.Text) <= 0 ||
            (asset?.WalletUnits ?? 0) < ParseUnits(_transferQuantity.Text) || _recipient.Text.Length == 0;
    }
    private void Transfer(bool confirm)
    {
        var units = ParseUnits(_transferQuantity.Text);
        if (units == 0) { _status.Text = L("error-quantity"); return; }
        _confirmTransfer.Disabled = true; _status.Text = L("processing");
        Request?.Invoke(confirm ? ExchangeAction.Transfer : ExchangeAction.PreviewTransfer, Assets[_transferCurrency.SelectedId],
            units, confirm ? _snapshot?.TransferQuote ?? Guid.Empty : Guid.Empty, _period, _recipient.Text.Trim().ToUpperInvariant(), "", "");
    }
    private void RenderWallet()
    {
        if (_snapshot is not { } s) return;
        _walletHeading.Text = L(s.WalletUnlocked ? "wallet" : "wallet-locked");
        _addressDisplay.Text = s.Address;
        _walletOwner.Text = s.WalletCharacter; _walletOwner.ToolTip = s.WalletCharacter;
        var value = s.Assets.Sum(a => a.WalletUnits / 1000d * a.Price);
        _portfolioValue.Text = s.WalletUnlocked && !_hideBalances ? "≈ " + Money(value) : "••••••";
        if (_lastWallet != s.Address || _lastUnlocked && !s.WalletUnlocked)
        {
            if (_lastWallet.Length == 0 && s.NeedsPassword) _walletTabs.CurrentTab = 2;
            _lastWallet = s.Address; _loginAddress.Text = s.Address;
            _creatingWallet = false; _showLogin = !s.WalletUnlocked;
            ClearPasswords(); InvalidateTransfer();
        }
        if (_loginSubmitted && s.WalletUnlocked) { _showLogin = false; _creatingWallet = false; _loginSubmitted = false; }
        _lastUnlocked = s.WalletUnlocked;
        _ownedWallets.Clear();
        for (var i = 0; i < s.Wallets.Length; i++)
        {
            var item = s.Wallets[i]; _ownedWallets.AddItem(L("wallet") + " " + item.Name + " · " + item.Address, i);
            if (item.Address == s.Address) _ownedWallets.SelectId(i);
        }
        if (s.Wallets.All(w => w.Address != s.Address))
        { _ownedWallets.AddItem(s.Address + " · " + s.WalletCharacter, s.Wallets.Length); _ownedWallets.SelectId(s.Wallets.Length); }
        _walletFunds.RemoveAllChildren();
        for (var i = 0; i < s.Assets.Length; i++)
        {
            var asset = s.Assets[i]; var index = Array.IndexOf(Assets, asset.Id);
            var balance = Column(); balance.Margin = new Thickness(0);
            var symbol = WalletLabel(asset.Symbol, true); symbol.FontColorOverride = Colours[index]; balance.AddChild(symbol);
            balance.AddChild(WalletLabel(!_hideBalances ? Coins(asset.WalletUnits) : "••••", true));
            var estimated = WalletLabel(!_hideBalances ? "≈ " + Money(asset.WalletUnits / 1000d * asset.Price) : "—");
            estimated.FontColorOverride = WalletMuted; balance.AddChild(estimated);
            var send = new Button { Text = L("send-coins"), HorizontalExpand = true };
            send.OnPressed += _ => { _transferCurrency.SelectId(index); InvalidateTransfer(); _walletSections.CurrentTab = 1; };
            balance.AddChild(send); _walletFunds.AddChild(WalletCard(balance));
        }
        _walletSections.Visible = s.WalletUnlocked;
        _sendCoins.Disabled = _tradeCoins.Disabled = _newWallet.Disabled = _logout.Disabled = _changePassword.Disabled = !s.WalletUnlocked;
        _withdrawals.RemoveAllChildren();
        if (s.Withdrawals.Length == 0) _withdrawals.AddChild(WalletLabel(L("withdrawal-empty")));
        foreach (var item in s.Withdrawals)
        {
            var entry = Column(); entry.Margin = new Thickness(0);
            var row = Row(); var date = WalletLabel(Time(item.At)); date.FontColorOverride = WalletMuted; row.AddChild(date);
            row.AddChild(new Label { Text = _hideBalances ? "••••" : Money(item.Amount), FontColorOverride = Colours[0] }); entry.AddChild(row);
            var recipient = L(item.Cash ? "cash" : "bank-account") + " → " + item.AccountOwner;
            var owner = WalletLabel(recipient); owner.ToolTip = recipient; entry.AddChild(owner);
            _withdrawals.AddChild(WalletCard(entry));
        }
        var matches = s.TransferQuote != Guid.Empty && s.TransferAsset == Assets[_transferCurrency.SelectedId] &&
            s.TransferUnits == ParseUnits(_transferQuantity.Text) && s.RecipientAddress.Equals(_recipient.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        _confirmTransfer.Disabled = !s.WalletUnlocked || !matches;
        _transferPreview.Text = matches ? s.RecipientCharacter + " · " + s.RecipientAddress + " · " + Coins(s.TransferUnits) + " " +
            s.Assets.First(a => a.Id == s.TransferAsset).Symbol : L("transfer-review");
        _transferPreview.ToolTip = _transferPreview.Text;
        UpdateTransferAvailability();
        RenderLogin();
    }

    /// <summary>Keep normal LineEdit editing/selection, while rendering and copying only masking characters.</summary>
    private sealed class PasswordField : LineEdit
    {
        public string Secret { get; private set; } = "";
        public PasswordField()
        {
            IsValid = text => text.Length <= 128;
            OnTextChanged += e =>
            {
                var oldMask = new string('•', Secret.Length);
                var prefix = 0;
                while (prefix < Math.Min(oldMask.Length, e.Text.Length) && oldMask[prefix] == e.Text[prefix]) prefix++;
                var suffix = 0;
                while (suffix < oldMask.Length - prefix && suffix < e.Text.Length - prefix && oldMask[^(suffix + 1)] == e.Text[^(suffix + 1)]) suffix++;
                if (e.Text.Length < oldMask.Length && e.Text.All(c => c == '•'))
                {
                    Secret = Secret.Remove(CursorPosition, oldMask.Length - e.Text.Length);
                    return;
                }
                Secret = Secret[..prefix] + e.Text.Substring(prefix, e.Text.Length - prefix - suffix) + Secret[(Secret.Length - suffix)..];
                var cursor = CursorPosition;
                Text = new string('•', Secret.Length);
                CursorPosition = cursor;
            };
        }
        public void ClearSecret() { Secret = ""; Text = ""; }
    }
}

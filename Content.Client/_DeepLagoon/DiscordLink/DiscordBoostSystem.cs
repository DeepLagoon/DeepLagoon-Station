using Content.Shared._DeepLagoon.DiscordLink;
using Robust.Client;
using Robust.Shared.Player;

namespace Content.Client._DeepLagoon.DiscordLink;

public sealed partial class DiscordBoostSystem : SharedDiscordBoostSystem
{
    [Dependency] private Robust.Client.Player.IPlayerManager _players = default!;
    [Dependency] private IBaseClient _client = default!;
    private DateTime _expiresAt;
    private DateTime _boostyExpiresAt;
    private int _boostyTier;
    private bool _spark;
    private int _purchasedTier;
    private DateTime _purchasedExpiresAt;
    private DateTime _sparkExpiresAt;
    public override int[] RoleColors { get; } = (int[]) DefaultRoleColors.Clone();

    public override void Initialize()
    {
        base.Initialize();
        _client.RunLevelChanged += OnRunLevelChanged;
        SubscribeNetworkEvent<DiscordBoostStatusEvent>(args =>
        {
            _expiresAt = DateTime.UtcNow.AddSeconds(Math.Clamp(args.RemainingSeconds, 0, 900));
            _boostyExpiresAt = DateTime.UtcNow.AddSeconds(Math.Clamp(args.BoostyRemainingSeconds, 0, 900));
            _boostyTier = Math.Clamp(args.BoostyTier, 0, 3);
            _spark = args.Spark;
            _sparkExpiresAt = DateTime.UtcNow.AddSeconds(900);
            _purchasedTier = Math.Clamp(args.PurchasedTier, 0, 4);
            _purchasedExpiresAt = DateTime.UtcNow.AddSeconds(Math.Clamp(args.PurchasedSeconds, 0, 900));
            if (args.RoleColors.Length == 4)
                Array.Copy(args.RoleColors, RoleColors, 4);
        });
    }

    public override bool HasActiveBoost(ICommonSession? session)
        => session != null && session == _players.LocalSession && DateTime.UtcNow < _expiresAt;

    public override int GetBoostyTier(ICommonSession? session)
        => session != null && session == _players.LocalSession && DateTime.UtcNow < _boostyExpiresAt ? _boostyTier : 0;

    public override bool HasSpark(ICommonSession? session) => session != null && session == _players.LocalSession && _spark && DateTime.UtcNow < _sparkExpiresAt;
    public override int GetPurchasedProductTier(ICommonSession? session) => session != null && session == _players.LocalSession && DateTime.UtcNow < _purchasedExpiresAt ? _purchasedTier : 0;
    private void Reset()
    {
        _expiresAt = _boostyExpiresAt = default;
        _boostyTier = _purchasedTier = 0;
        _spark = false;
        _sparkExpiresAt = _purchasedExpiresAt = default;
        Array.Copy(DefaultRoleColors, RoleColors, 4);
    }

    private void OnRunLevelChanged(object? sender, RunLevelChangedEventArgs args)
    {
        if (args.NewLevel == ClientRunLevel.Initialize)
            Reset();
    }

    public override void Shutdown()
    {
        Reset();
        _client.RunLevelChanged -= OnRunLevelChanged;
        base.Shutdown();
    }
}

using Content.Shared.Administration;
using Content.Shared.Administration.Managers;
using Robust.Shared.Player;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.DiscordLink;

/// <summary>Server checks the linked Discord identity; client receives only its own status.</summary>
public abstract partial class SharedDiscordBoostSystem : EntitySystem
{
    [Dependency] private ISharedAdminManager _admins = default!;

    public bool HasHostAccess(ICommonSession? session)
        => session != null && _admins.HasAdminFlag(session, AdminFlags.Host, includeDeAdmin: true);

    // Effective feature entitlement; the actual Discord/Boosty subscription stays unchanged.
    public int GetSupporterTier(ICommonSession? session)
        => HasHostAccess(session) ? 3 : Content.Shared.Database.LagoonEconomyRules.GameTier(GetProductTier(session));

    // Spark is product tier 1, which maps to game feature tier 0.
    public bool CanUseMarkingGlow(ICommonSession? session) => GetSupporterTier(session) > 0;

    public abstract bool HasActiveBoost(ICommonSession? session);
    public abstract int GetBoostyTier(ICommonSession? session);
    public abstract int[] RoleColors { get; }
    public virtual bool HasSpark(ICommonSession? session) => false;
    public virtual int GetPurchasedProductTier(ICommonSession? session) => 0;
    public int GetProductTier(ICommonSession? session) => Math.Max(GetPurchasedProductTier(session),
        Content.Shared.Database.LagoonEconomyRules.ProductTier(GetBoostyTier(session), HasSpark(session), HasActiveBoost(session)));
    public bool HasOocColorAccess(ICommonSession? session) => GetProductTier(session) > 0;

    // An active Discord boost grants the Impulse feature level; higher levels take precedence.
    public static int LoadoutPointBonus(bool discordBooster, int boostyTier)
        => boostyTier > 0 ? 3 * (Math.Clamp(boostyTier, 1, 3) + 1) : discordBooster ? 6 : 0;

    public int GetLoadoutPointBonus(ICommonSession? session)
        => LoadoutPointBonus(HasActiveBoost(session), GetSupporterTier(session));

    public bool HasDiscordRewardAccess(ICommonSession? session)
        => HasActiveBoost(session) || GetSupporterTier(session) > 0;

    public bool CanUseDonorItem(DonorCategory category, int tier, ICommonSession? session)
        => category switch
        {
            DonorCategory.None => true,
            DonorCategory.DiscordBoost => HasDiscordRewardAccess(session),
            DonorCategory.Boosty => tier is >= 1 and <= 3 && GetSupporterTier(session) >= tier,
            _ => false,
        };

    public Color GetDonorColor(DonorCategory category, int tier)
    {
        var index = category == DonorCategory.DiscordBoost ? 0 : Math.Clamp(tier, 1, 3);
        var rgb = RoleColors[index];
        if (rgb == 0)
            rgb = DefaultRoleColors[index];
        return Color.FromHex($"#{rgb:X6}");
    }

    // Discord's default color is 0, meaning no assigned color, not black.
    public static readonly int[] DefaultRoleColors = { 0xF47FFF, 0x5DADE2, 0xA780DE, 0xE6B450 };
}

public enum DonorCategory : byte { None, DiscordBoost, Boosty }

[Serializable, NetSerializable]
public sealed class DiscordBoostStatusEvent(int remainingSeconds, int boostyTier = 0, int boostyRemainingSeconds = 0, int[]? roleColors = null, bool spark = false, int purchasedTier = 0, int purchasedSeconds = 0) : EntityEventArgs
{
    public int RemainingSeconds = remainingSeconds;
    public int BoostyTier = boostyTier;
    public bool Spark = spark;
    public int PurchasedTier = purchasedTier;
    public int PurchasedSeconds = purchasedSeconds;
    public int BoostyRemainingSeconds = boostyRemainingSeconds;
    public int[] RoleColors = roleColors ?? SharedDiscordBoostSystem.DefaultRoleColors;
}

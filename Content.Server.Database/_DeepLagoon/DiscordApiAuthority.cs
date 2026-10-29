using System.Linq;

namespace Content.Server.Database;

public static class DiscordApiAuthority
{
    public static bool CanManageStaff(Admin? admin, string operation, string? kind, string? discordRole, string? applicationId)
    {
        // Only the authenticated moderation bot can attest these fresh Discord roles.
        // This delegation never grants arbitrary flags, bans, notes or currency access.
        if (admin is { Suspended: true } or { Deadminned: true } ||
            operation is not ("staff_add" or "staff_remove" or "staff_status") ||
            kind is not ("admin" or "trial" or "mapper")) return false;
        var host = discordRole == "1554164947804487770";
        var head = kind == "mapper" ? discordRole == "1555959349615263804" : discordRole == "1554887617831764060";
        if (!host && !head) return false;
        if (operation != "staff_add" || host) return true;
        return applicationId is { Length: >= 15 and <= 20 } && applicationId.All(char.IsAsciiDigit) &&
               ulong.TryParse(applicationId, out var id) && id > 0;
    }

    public static bool Has(Admin? admin, string permission)
    {
        if (admin == null || admin.Suspended || admin.Deadminned) return false;
        var flags = (admin.AdminRank?.Flags.Select(f => f.Flag) ?? [])
            .Concat(admin.Flags?.Where(f => !f.Negative).Select(f => f.Flag) ?? []).ToHashSet();
        flags.ExceptWith(admin.Flags?.Where(f => f.Negative).Select(f => f.Flag) ?? []);
        return flags.Contains("HOST") || flags.Contains(permission);
    }
}

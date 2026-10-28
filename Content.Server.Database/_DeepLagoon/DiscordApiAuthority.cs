using System.Linq;

namespace Content.Server.Database;

public static class DiscordApiAuthority
{
    public static bool Has(Admin? admin, string permission)
    {
        if (admin == null || admin.Suspended || admin.Deadminned) return false;
        var flags = (admin.AdminRank?.Flags.Select(f => f.Flag) ?? [])
            .Concat(admin.Flags?.Where(f => !f.Negative).Select(f => f.Flag) ?? []).ToHashSet();
        flags.ExceptWith(admin.Flags?.Where(f => f.Negative).Select(f => f.Flag) ?? []);
        return flags.Contains("HOST") || flags.Contains(permission);
    }
}

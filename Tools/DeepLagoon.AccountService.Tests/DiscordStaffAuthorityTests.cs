using Content.Server.Database;
using NUnit.Framework;

namespace DeepLagoon.AccountService.Tests;

[TestFixture]
public sealed class DiscordStaffAuthorityTests
{
    private const string Host = "1554164947804487770";
    private const string HeadAdmin = "1554887617831764060";
    private const string HeadMapper = "1555959349615263804";
    private const string Application = "1565299000000000000";

    [Test]
    public void DepartmentDelegationDoesNotRequireBroadGamePermissions()
    {
        foreach (var admin in new Admin?[] { null, new() { Flags = [] } })
        {
            Assert.That(DiscordApiAuthority.Has(admin, "PERMISSIONS"), Is.False);
            foreach (var (role, kind) in new[] { (HeadAdmin, "admin"), (HeadAdmin, "trial"), (HeadMapper, "mapper") })
            {
                Assert.That(DiscordApiAuthority.CanManageStaff(admin, "staff_add", kind, role, Application), Is.True);
                Assert.That(DiscordApiAuthority.CanManageStaff(admin, "staff_status", kind, role, null), Is.True);
                Assert.That(DiscordApiAuthority.CanManageStaff(admin, "staff_remove", kind, role, null), Is.True);
                Assert.That(DiscordApiAuthority.CanManageStaff(admin, "staff_add", kind, role, null), Is.False);
            }
            foreach (var kind in new[] { "admin", "trial", "mapper" })
                Assert.That(DiscordApiAuthority.CanManageStaff(admin, "staff_add", kind, Host, null), Is.True);
        }
    }

    [Test]
    public void CrossDepartmentUnknownRoleMalformedContextAndOtherOperationsAreDenied()
    {
        foreach (var (role, kind) in new[] { (HeadAdmin, "mapper"), (HeadMapper, "admin"), (HeadMapper, "trial"),
                     ("1554196521980006540", "admin"), ("HOST", "admin"), (Host, "host"), (HeadAdmin, "") })
            Assert.That(DiscordApiAuthority.CanManageStaff(null, "staff_add", kind, role, Application), Is.False);
        foreach (var role in new[] { Host, HeadAdmin, HeadMapper })
            foreach (var operation in new[] { "staff_flags", "admin_flags", "ban", "notes", "lc_add", "apartment_grant", "unknown" })
                Assert.That(DiscordApiAuthority.CanManageStaff(null, operation, "admin", role, Application), Is.False);
        foreach (var id in new[] { "", "0", "123", "156529900000000000x", "999999999999999999999", "١٥٦٥٢٩٩٠٠٠٠٠٠٠٠٠٠٠٠" })
            Assert.That(DiscordApiAuthority.CanManageStaff(null, "staff_add", "admin", HeadAdmin, id), Is.False);
    }

    [Test]
    public void SuspendedAndDeadminnedActorsCannotUseDelegationEvenAsHost()
    {
        foreach (var admin in new[] { new Admin { Suspended = true }, new Admin { Deadminned = true } })
            foreach (var role in new[] { Host, HeadAdmin, HeadMapper })
                foreach (var operation in new[] { "staff_add", "staff_remove", "staff_status" })
                    Assert.That(DiscordApiAuthority.CanManageStaff(admin, operation, "mapper", role, Application), Is.False);
    }
}

using System.Security.AccessControl;
using System.Security.Principal;
using Shouldly;

namespace PowerLedger.Service.Tests;

public sealed class DataDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"powerledger-dir-{Guid.NewGuid():N}");

    [Fact]
    public void A_console_run_just_creates_the_folders()
    {
        var paths = new ServicePaths(_root);
        DataDirectory.Prepare(paths, enforce: false, DateTimeOffset.UtcNow).ShouldBeNull();
        Directory.Exists(paths.DataDirectory).ShouldBeTrue();
        Directory.Exists(paths.Logs).ShouldBeTrue();
        paths.Database.ShouldBe(Path.Combine(_root, "power.db"));
        // Data sharing's copies of what was sent, which the App reads, and the service's crash files, under the folder's ACL.
        (paths.Sent, paths.Crashes).ShouldBe((Path.Combine(_root, "Sent"), Path.Combine(_root, "Crashes")));
        Directory.Exists(paths.Sent).ShouldBeTrue();
        Directory.Exists(paths.Crashes).ShouldBeTrue();
    }

    [Fact]
    public void A_folder_owned_by_an_ordinary_account_is_refused()
    {
        // The test runs as an ordinary user, so the folder it creates belongs to that user: the planted-folder case.
        Directory.CreateDirectory(_root);
        var owner = new DirectoryInfo(_root).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (DataDirectory.IsTrustedOwner(owner)) return;   // an elevated run owns it as Administrators; nothing to prove
        Should.Throw<UntrustedDataDirectoryException>(() => DataDirectory.Prepare(new ServicePaths(_root), enforce: true, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Only_system_and_administrators_are_trusted_owners()
    {
        DataDirectory.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)).ShouldBeTrue();
        DataDirectory.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)).ShouldBeTrue();
        DataDirectory.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)).ShouldBeFalse();
        DataDirectory.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)).ShouldBeFalse();
        DataDirectory.IsTrustedOwner(null).ShouldBeFalse();
    }

    [Fact]
    public void The_folder_acl_lets_users_read_and_nobody_else_write()
    {
        var security = DataDirectory.Security();
        security.AreAccessRulesProtected.ShouldBeTrue();
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        rules.Count.ShouldBe(3);
        rules.ShouldAllBe(r => r.AccessControlType == AccessControlType.Allow
            && r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
        Rights(rules, WellKnownSidType.LocalSystemSid).ShouldBe(FileSystemRights.FullControl);
        Rights(rules, WellKnownSidType.BuiltinAdministratorsSid).ShouldBe(FileSystemRights.FullControl);
        Rights(rules, WellKnownSidType.BuiltinUsersSid).ShouldBe(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static FileSystemRights Rights(List<FileSystemAccessRule> rules, WellKnownSidType who)
        => rules.Single(r => r.IdentityReference == new SecurityIdentifier(who, null)).FileSystemRights;
}

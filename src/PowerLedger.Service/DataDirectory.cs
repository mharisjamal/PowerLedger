using System.Security.AccessControl;
using System.Security.Principal;

namespace PowerLedger.Service;

/// <summary>
/// The data folder's security (spec §7, §11). The service runs as LocalSystem and opens the database, so the folder
/// and everything in it are writable by SYSTEM and administrators only: a database users could edit would feed crafted
/// input to a SYSTEM process. Users get read access, which is all the App's read-only connection needs while the
/// service holds the write-ahead log open. A folder or database file that another account owns was not made by
/// PowerLedger or its installer, and is not trusted.
/// </summary>
internal static class DataDirectory
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>
    /// Creates the folders. When <paramref name="enforce"/> is set, as it is under the service control manager, refuses a
    /// folder with an untrusted owner, applies the ACL, and sets aside database files with an untrusted owner.
    /// </summary>
    /// <returns>A note for the status screen when database files were set aside; otherwise null.</returns>
    public static string? Prepare(ServicePaths paths, bool enforce, DateTimeOffset now)
    {
        var folder = new DirectoryInfo(paths.DataDirectory);
        folder.Create();
        if (enforce)
        {
            var owner = folder.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (!IsTrustedOwner(owner)) throw new UntrustedDataDirectoryException(paths.DataDirectory, owner);
            folder.SetAccessControl(Security());
        }
        Directory.CreateDirectory(paths.Logs);
        if (!enforce) return null;

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = new FileInfo(paths.Database + suffix);
            if (!file.Exists) continue;
            var fileOwner = file.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (IsTrustedOwner(fileOwner)) continue;
            var aside = DatabaseOpener.SetAside(paths.Database, "untrusted", now);
            return $"A database file owned by {fileOwner?.Value ?? "nobody"} was set aside as {Path.GetFileName(aside)} and a new database started.";
        }
        return null;
    }

    /// <summary>SYSTEM and Administrators are the only owners a PowerLedger folder or database may have.</summary>
    public static bool IsTrustedOwner(SecurityIdentifier? owner) => owner == System || owner == Administrators;

    /// <summary>SYSTEM and Administrators full control, Users read, all inherited by everything inside; nothing from above.</summary>
    public static DirectorySecurity Security()
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}

/// <summary>The data folder belongs to an account that is neither SYSTEM nor Administrators, so it may have been planted.</summary>
internal sealed class UntrustedDataDirectoryException(string path, SecurityIdentifier? owner)
    : Exception($"{path} is owned by {owner?.Value ?? "nobody"}, not by SYSTEM or Administrators, so PowerLedger will not keep its data there. Delete the folder and start the service again.");

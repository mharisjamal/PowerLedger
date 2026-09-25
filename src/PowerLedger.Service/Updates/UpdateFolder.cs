using System.Security.AccessControl;
using System.Security.Principal;
using PowerLedger.Updates;

namespace PowerLedger.Service.Updates;

/// <summary>
/// Where the service keeps downloaded installers (Plan Q §4): <c>%ProgramData%\PowerLedger\Updates</c>, for SYSTEM and
/// Administrators alone, so no user can swap a file the service is about to run. Checked before every use: a link in the
/// folder's place is removed, a folder another account owns is refused, and a folder whose ACL someone changed is locked
/// again and emptied, since anything in it may have been put there while it was open.
/// </summary>
/// <param name="path">The folder.</param>
/// <param name="allowed">Who gets full control; nobody else gets anything.</param>
/// <param name="trustedOwner">Which owners the folder may have.</param>
internal sealed class UpdateFolder(string path, IReadOnlyList<SecurityIdentifier> allowed, Func<SecurityIdentifier?, bool> trustedOwner)
{
    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>The service's folder: SYSTEM and Administrators, owned by one of them.</summary>
    public static UpdateFolder ForService(string path) => new(
        path,
        [new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)],
        DataDirectory.IsTrustedOwner);

    /// <summary>The folder's full path.</summary>
    public string Path { get; } = path;

    /// <summary>Full control for each of <paramref name="who"/>, inherited by everything inside; nothing from above.</summary>
    public static DirectorySecurity Security(IEnumerable<SecurityIdentifier> who)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in who)
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>Makes the folder, or checks and repairs it. Throws <see cref="UpdateException"/> when it can't be trusted.</summary>
    public void Prepare()
    {
        try
        {
            var folder = new DirectoryInfo(Path);
            if (folder.Exists && folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                folder.Delete();                                   // the link alone, never what it points at
                folder.Refresh();
            }
            if (!folder.Exists)
            {
                FileSystemAclExtensions.Create(folder, Security(allowed));
                return;
            }
            var security = folder.GetAccessControl();
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (!trustedOwner(owner)) throw new UpdateException($"{Path} is owned by {owner?.Value ?? "nobody"}, so updates aren't kept there.");
            if (Locked(security)) return;
            folder.SetAccessControl(Security(allowed));
            Empty(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new UpdateException($"The updates folder couldn't be prepared: {error.Message}", error);
        }
    }

    /// <summary>Protected, and nothing but full control for each allowed account.</summary>
    private bool Locked(DirectorySecurity security)
    {
        if (!security.AreAccessRulesProtected) return false;
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        return rules.Count == allowed.Count
            && rules.All(rule => rule.AccessControlType == AccessControlType.Allow && !rule.IsInherited
                && rule.FileSystemRights == FileSystemRights.FullControl && rule.InheritanceFlags == Inherit
                && allowed.Contains((SecurityIdentifier)rule.IdentityReference))
            && allowed.All(sid => rules.Any(rule => (SecurityIdentifier)rule.IdentityReference == sid));
    }

    /// <summary>Deletes everything inside: links as links, folders with what they hold.</summary>
    private static void Empty(DirectoryInfo folder)
    {
        foreach (var entry in folder.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo inner && !inner.Attributes.HasFlag(FileAttributes.ReparsePoint)) inner.Delete(recursive: true);
            else entry.Delete();
        }
    }
}

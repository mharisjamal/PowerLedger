using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using PowerLedger.Service.Updates;
using PowerLedger.Updates;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>
/// Plan Q §4's guards on a SYSTEM service running a downloaded installer: the release signature, the Updates folder's ACL,
/// and the check made while the file is held open. Each test makes its own key pair; none touches the owner's key.
/// </summary>
public sealed class UpdateSecurityTests : IDisposable
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"powerledger-updates-{Guid.NewGuid():N}");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public UpdateSecurityTests() => Directory.CreateDirectory(_root);

    private string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    private const string Version = "0.9.1";
    private const string FileName = "PowerLedger-0.9.1-setup-x64.exe";

    /// <summary>The release key's signature of what the release script signs: "PowerLedger|version|file name|sha256 hex".</summary>
    private byte[] Sign(byte[] sha256, string version = Version, string fileName = FileName)
        => _key.SignData(
            Encoding.UTF8.GetBytes($"PowerLedger|{version}|{fileName}|{Convert.ToHexStringLower(sha256)}"), HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);

    // ---- The signature

    [Fact]
    public void A_signature_made_with_the_release_key_verifies()
    {
        var digest = SHA256.HashData("setup"u8);
        ReleaseSignature.Verifies(PublicKey, Version, FileName, digest, Sign(digest)).ShouldBeTrue();
    }

    [Fact]
    public void A_signature_from_another_key_or_over_another_file_does_not()
    {
        var digest = SHA256.HashData("setup"u8);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var otherKeys = other.SignData(
            Encoding.UTF8.GetBytes($"PowerLedger|{Version}|{FileName}|{Convert.ToHexStringLower(digest)}"), HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        ReleaseSignature.Verifies(PublicKey, Version, FileName, digest, otherKeys).ShouldBeFalse();
        ReleaseSignature.Verifies(PublicKey, Version, FileName, SHA256.HashData("other"u8), Sign(digest)).ShouldBeFalse();
    }

    [Fact]
    public void A_signature_for_another_version_or_file_name_does_not_verify()
    {
        var digest = SHA256.HashData("setup"u8);
        ReleaseSignature.Verifies(PublicKey, "0.9.2", FileName, digest, Sign(digest)).ShouldBeFalse();
        ReleaseSignature.Verifies(PublicKey, Version, "PowerLedger-0.9.1-setup-arm64.exe", digest, Sign(digest)).ShouldBeFalse();
        ReleaseSignature.Verifies(PublicKey, Version, FileName, digest, _key.SignHash(digest, DSASignatureFormat.Rfc3279DerSequence))
            .ShouldBeFalse();                                                    // the bare digest, as before, no longer does
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void No_key_or_a_broken_one_verifies_nothing(string key)
    {
        var digest = SHA256.HashData("setup"u8);
        ReleaseSignature.Verifies(key, Version, FileName, digest, Sign(digest)).ShouldBeFalse();
    }

    [Fact]
    public void Garbage_for_a_signature_verifies_nothing()
        => ReleaseSignature.Verifies(PublicKey, Version, FileName, SHA256.HashData("setup"u8), [1, 2, 3]).ShouldBeFalse();

    /// <summary>What scripts\release-signing.ps1 made with a throwaway key for release 0.9.1 over
    /// PowerLedger-0.9.1-setup-x64.exe holding "hello\n", so the script's format and the service's check can't drift apart.</summary>
    [Fact]
    public void A_signature_from_release_ps1_verifies()
    {
        const string key = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEgHudJmQ8XXPa/59qFS8tqd8NnO3Zn26BEL/n60q3nwd0xp43A1iyuK3sTvbtmVJKmykElFDb4h5ai5iGrQgZ0w==";
        var signature = Convert.FromBase64String("MEUCIHquDJrRV7g6u6x8uyQPhaAgbVJhucI+VTj6Q9H/He+UAiEAyRK0i8nEKdy+H2xCMaEMFn5nChLrfMJXZt4/33znm5Q=");
        ReleaseSignature.Verifies(key, "0.9.1", FileName, SHA256.HashData("hello\n"u8), signature).ShouldBeTrue();
        ReleaseSignature.Verifies(key, "0.9.1", FileName, SHA256.HashData("hello"u8), signature).ShouldBeFalse();
        ReleaseSignature.Verifies(key, "0.9.2", FileName, SHA256.HashData("hello\n"u8), signature).ShouldBeFalse();
    }

    [Fact]
    public void The_signatures_file_gives_each_installers_signature()
    {
        var json = Encoding.UTF8.GetBytes("""{ "PowerLedger-0.9.1-setup-x64.exe": "AQID", "PowerLedger-0.9.1-setup.exe": "BAUG" }""");
        ReleaseSignature.For(json, "PowerLedger-0.9.1-setup-x64.exe").ShouldBe([1, 2, 3]);
        ReleaseSignature.For(json, "PowerLedger-0.9.1-setup-arm64.exe").ShouldBeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "PowerLedger-0.9.1-setup-x64.exe": 12 }""")]
    [InlineData("""{ "PowerLedger-0.9.1-setup-x64.exe": "not base64!" }""")]
    [InlineData("""["PowerLedger-0.9.1-setup-x64.exe"]""")]
    public void A_signatures_file_that_isnt_one_gives_nothing(string text)
        => ReleaseSignature.For(Encoding.UTF8.GetBytes(text), "PowerLedger-0.9.1-setup-x64.exe").ShouldBeNull();

    // ---- Opening the App in the user's session

    /// <summary>CreateProcessAsUser may write into its command line, so it gets a buffer of its own, ended by a null, and
    /// never a string's own memory.</summary>
    [Fact]
    public void The_Apps_command_line_is_a_writable_buffer_ended_by_a_null()
    {
        var line = WindowsUpdateSystem.CommandLine(@"C:\Program Files\PowerLedger\PowerLedger.exe", "--after-update --tray");
        line.ShouldBe((@"""C:\Program Files\PowerLedger\PowerLedger.exe"" --after-update --tray" + "\0").ToCharArray());
        var parameter = typeof(WindowsUpdateSystem).GetMethod("CreateProcessAsUser", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetParameters().Single(p => p.Name == "commandLine");
        parameter.ParameterType.ShouldBe(typeof(char[]));
    }

    // ---- The check made while the file is held

    private (string Path, byte[] Sha256) Installer(string content = "an installer")
    {
        var path = Path.Combine(_root, FileName);
        File.WriteAllText(path, content);
        return (path, SHA256.HashData(File.ReadAllBytes(path)));
    }

    /// <summary>Opens <paramref name="path"/> as release 0.9.1's x64 installer, listed with this size and SHA-256.</summary>
    private static VerifiedInstaller Open(string path, long size, byte[] sha256, byte[] signature, string publicKey)
        => VerifiedInstaller.Open(
            path, new Release(global::System.Version.Parse(Version), new Uri("https://example.com/"), new Uri("https://example.com/i"), FileName, size, sha256),
            signature, publicKey);

    [Fact]
    public void An_installer_signed_for_another_version_is_refused()
    {
        var (path, sha) = Installer();
        Should.Throw<UpdateException>(() => Open(path, new FileInfo(path).Length, sha, Sign(sha, version: "0.9.0"), PublicKey))
            .Message.ShouldContain("signature");
    }

    [Fact]
    public void A_verified_installer_is_held_so_nobody_can_change_or_replace_it()
    {
        var (path, sha) = Installer();
        using (var held = Open(path, new FileInfo(path).Length, sha, Sign(sha), PublicKey))
        {
            held.Path.ShouldBe(path);
            Should.Throw<IOException>(() => File.OpenWrite(path).Dispose());
            Should.Throw<IOException>(() => File.Delete(path));
            Should.Throw<IOException>(() => File.Move(path, path + ".old"));
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);   // setup itself can still read it
        }
        File.OpenWrite(path).Dispose();                                                             // let go once disposed
    }

    [Fact]
    public void A_file_already_open_for_writing_is_not_run()
    {
        var (path, sha) = Installer();
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Should.Throw<UpdateException>(() => Open(path, new FileInfo(path).Length, sha, Sign(sha), PublicKey));
    }

    [Fact]
    public void A_file_of_another_size_or_digest_is_refused_and_let_go()
    {
        var (path, sha) = Installer();
        var size = new FileInfo(path).Length;
        Should.Throw<UpdateException>(() => Open(path, size + 1, sha, Sign(sha), PublicKey));
        var otherSha = SHA256.HashData("something else"u8);
        Should.Throw<UpdateException>(() => Open(path, size, otherSha, Sign(otherSha), PublicKey));
        File.OpenWrite(path).Dispose();
    }

    [Fact]
    public void A_file_whose_signature_doesnt_verify_is_refused()
    {
        var (path, sha) = Installer();
        var size = new FileInfo(path).Length;
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Should.Throw<UpdateException>(() => Open(path, size, sha, other.SignHash(sha, DSASignatureFormat.Rfc3279DerSequence), PublicKey))
            .Message.ShouldContain("signature");
        Should.Throw<UpdateException>(() => Open(path, size, sha, Sign(sha), ""));   // no key built in: never
    }

    [Fact]
    public void A_file_changed_after_the_download_was_checked_is_refused()
    {
        var (path, sha) = Installer("an installer");
        var size = new FileInfo(path).Length;
        File.WriteAllText(path, "an installEr");                                                    // same size, other bytes
        Should.Throw<UpdateException>(() => Open(path, size, sha, Sign(sha), PublicKey));
    }

    [Fact]
    public void A_missing_installer_is_an_update_failure()
        => Should.Throw<UpdateException>(() => Open(Path.Combine(_root, "gone.exe"), 1, new byte[32], [], PublicKey));

    // ---- The folder

    private UpdateFolder Folder(Func<SecurityIdentifier?, bool>? trusted = null)
        => new(Path.Combine(_root, "Updates"), [Me, System], trusted ?? (owner => owner == Me));

    private static AuthorizationRuleCollection Rules(string path)
        => new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));

    [Fact]
    public void The_services_folder_is_for_SYSTEM_and_administrators_alone()
    {
        var security = UpdateFolder.Security([System, Administrators]);
        security.AreAccessRulesProtected.ShouldBeTrue();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        rules.Select(rule => rule.IdentityReference).ShouldBe([System, Administrators], ignoreOrder: true);
        rules.ShouldAllBe(rule => rule.AccessControlType == AccessControlType.Allow && rule.FileSystemRights == FileSystemRights.FullControl);
        rules.ShouldAllBe(rule => rule.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
    }

    [Fact]
    public void The_folder_is_made_with_nothing_but_its_own_rules()
    {
        var folder = Folder();
        folder.Prepare();
        Directory.Exists(folder.Path).ShouldBeTrue();
        new DirectoryInfo(folder.Path).GetAccessControl().AreAccessRulesProtected.ShouldBeTrue();
        Rules(folder.Path).Cast<FileSystemAccessRule>().Select(rule => rule.IdentityReference).ShouldBe([Me, System], ignoreOrder: true);
    }

    [Fact]
    public void A_folder_someone_opened_up_is_locked_again_and_emptied()
    {
        var folder = Folder();
        folder.Prepare();
        var security = new DirectoryInfo(folder.Path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder.Path).SetAccessControl(security);
        var planted = Path.Combine(folder.Path, "PowerLedger-9.9.9-setup-x64.exe");
        File.WriteAllText(planted, "not ours");

        folder.Prepare();

        Rules(folder.Path).Cast<FileSystemAccessRule>().Select(rule => rule.IdentityReference).ShouldNotContain(Everyone);
        File.Exists(planted).ShouldBeFalse();
    }

    [Fact]
    public void A_folder_already_locked_keeps_its_downloads()
    {
        var folder = Folder();
        folder.Prepare();
        var kept = Path.Combine(folder.Path, "PowerLedger-0.9.1-setup-x64.exe");
        File.WriteAllText(kept, "ours");
        folder.Prepare();
        File.Exists(kept).ShouldBeTrue();
    }

    [Fact]
    public void A_folder_someone_else_owns_is_not_used()
    {
        var folder = Folder(trusted: _ => false);
        Directory.CreateDirectory(folder.Path);
        Should.Throw<UpdateException>(folder.Prepare).Message.ShouldContain("owned by");
    }

    [Fact]
    public void A_link_in_the_folders_place_is_replaced_by_a_real_folder_without_touching_where_it_pointed()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var target = Path.Combine(elsewhere, "keep.txt");
        File.WriteAllText(target, "keep");
        var folder = Folder();
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{folder.Path}\" \"{elsewhere}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            mklink.WaitForExit();
        new DirectoryInfo(folder.Path).Attributes.HasFlag(FileAttributes.ReparsePoint).ShouldBeTrue();

        folder.Prepare();

        new DirectoryInfo(folder.Path).Attributes.HasFlag(FileAttributes.ReparsePoint).ShouldBeFalse();
        File.Exists(target).ShouldBeTrue();
    }

    public void Dispose()
    {
        _key.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A temp folder left behind.
        }
    }
}

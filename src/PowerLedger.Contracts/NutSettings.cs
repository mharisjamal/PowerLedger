using System.Text.RegularExpressions;

namespace PowerLedger.Contracts;

/// <summary>
/// A UPS that another computer serves over Network UPS Tools (NUT), as the owner sets it up in Settings: the server, its
/// port, the UPS's name there, and a username and password for a server that lists only to known users. Off while the host
/// is blank. Whether its reading stands for this PC is the same answer as for a UPS on USB, <see cref="MachineProfile.UpsLoad"/>.
///
/// The password travels one way. The App sends it in <see cref="Password"/> only when the owner types one, an empty one to
/// forget it, and null to keep the one the service holds. The service keeps it encrypted apart from the settings and never
/// sends it back: settings from the service carry null there, and <see cref="HasPassword"/> says whether it holds one.
/// Init-only properties with initialisers, so settings stored by an older version take the default.
/// </summary>
public sealed partial record NutSettings
{
    public const int DefaultPort = 3493;

    /// <summary>The computer running NUT's upsd: a name or an address; blank for none.</summary>
    public string Host { get; init; } = "";

    public int Port { get; init; } = DefaultPort;

    /// <summary>The UPS's name on that server, as its ups.conf gives it, e.g. "myups".</summary>
    public string Ups { get; init; } = "";

    /// <summary>Blank to say no name to the server.</summary>
    public string Username { get; init; } = "";

    /// <summary>A new password from the App (empty to forget the one held), or null to keep it. Never stored here and never
    /// sent by the service.</summary>
    public string? Password { get; init; }

    /// <summary>Set by the service: it holds a password for this server.</summary>
    public bool HasPassword { get; init; }

    public static NutSettings Off { get; } = new();

    /// <summary>A host and a UPS are both given.</summary>
    public bool IsSetUp => Host.Trim().Length > 0 && Ups.Trim().Length > 0;

    /// <summary>Null when every value is acceptable; otherwise the first problem, in words the App can show.</summary>
    public string? Validate()
    {
        if (Host is null || Ups is null || Username is null) return "The UPS server's settings are missing.";
        if (Host.Length > 0 && !HostName().IsMatch(Host)) return "Type the UPS server as a computer name or an IP address.";
        if (Port is < 1 or > 65535) return "The UPS server's port must be between 1 and 65535.";
        if (Ups.Length > 0 && !UpsName().IsMatch(Ups)) return "Type the UPS's name as the server knows it, in letters, digits, dots, dashes or underscores.";
        if (Host.Length > 0 != Ups.Length > 0) return "Type both the UPS server and the UPS's name, or neither.";
        if (Username.Length > 64 || Username.Any(char.IsControl) || Username.Contains(' ')) return "The UPS server's username can't hold spaces and is at most 64 characters.";
        if (Password is { } password && (password.Length > 128 || password.Any(char.IsControl))) return "The UPS server's password is at most 128 characters.";
        return null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._:\-\[\]%]{1,253}$")]
    private static partial Regex HostName();

    [GeneratedRegex(@"^[A-Za-z0-9._\-]{1,64}$")]
    private static partial Regex UpsName();
}

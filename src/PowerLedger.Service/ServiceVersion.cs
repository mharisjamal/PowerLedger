using System.Reflection;
using System.Text.RegularExpressions;

namespace PowerLedger.Service;

/// <summary>The service's own version.</summary>
internal static partial class ServiceVersion
{
    /// <summary>As built, with the commit after a plus, e.g. <c>0.6.0+1a2b3c4</c>: what the status screen shows.</summary>
    public static string Informational { get; } =
        typeof(ServiceVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary><c>X.Y.Z</c> alone, as the data server takes a version.</summary>
    public static string Short { get; } = Plain(Informational);

    /// <summary>The leading <c>X.Y.Z</c> of a version, without the commit or a pre-release label the server's schema would
    /// refuse; <c>0.0.0</c> when it has none.</summary>
    public static string Plain(string? version) => version is not null && Leading().Match(version) is { Success: true } match ? match.Value : "0.0.0";

    [GeneratedRegex(@"^[0-9]{1,4}\.[0-9]{1,5}\.[0-9]{1,6}(?![0-9])")]
    private static partial Regex Leading();
}
